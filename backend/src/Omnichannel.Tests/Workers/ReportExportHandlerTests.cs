using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers;
using Omnichannel.Workers.ReportExportWorker;
using RabbitMQ.Client;

namespace Omnichannel.Tests.Workers;

/// <summary>
/// Task 020 C4: what the Report Export Worker does with one delivery, step by step, against doubles of the database
/// side and of the storage (the real ones are in the integration tests).
/// </summary>
public sealed class ReportExportHandlerTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();

    // ---- the normal delivery -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_export_is_built_stored_under_the_key_of_its_task_and_completed_in_that_order()
    {
        var exports = new FakeExports();
        var storage = new FakeStorage(exports.Calls);

        var result = await Handler(exports, storage).HandleAsync(Delivery(), CancellationToken.None);

        Assert.Equal(HandlerResult.Ack, result);
        Assert.Equal(["get", "start", "generate", "put", "complete"], exports.Calls);
        var stored = Assert.Single(storage.Puts);
        Assert.Equal(
            ($"exports/{TaskId}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "attachment; filename=\"orders_20261005-1030.xlsx\"", 2048L),
            (stored.Key, stored.ContentType, stored.ContentDisposition, stored.Length));
        // Size and row count of the file that was stored; the content is released afterwards.
        Assert.Equal((TaskId, exports.Attempt!.AttemptId, 2048L, 7), Assert.Single(exports.Completed));
        Assert.False(exports.Generated!.Content.CanRead);
        Assert.Empty(exports.TransientFailures);
    }

    [Theory]
    [InlineData(ProcessingTaskStatus.Succeeded)]
    [InlineData(ProcessingTaskStatus.Failed)]
    public async Task A_delivery_for_a_finished_task_is_acknowledged_without_an_attempt(ProcessingTaskStatus status)
    {
        var exports = new FakeExports { TaskState = State(status) };
        var storage = new FakeStorage(exports.Calls);

        var result = await Handler(exports, storage).HandleAsync(Delivery(), CancellationToken.None);

        Assert.Equal(HandlerResult.Ack, result);
        Assert.Equal(["get"], exports.Calls);
    }

    [Fact]
    public async Task A_task_completed_by_another_delivery_meanwhile_is_acknowledged_without_building()
    {
        var exports = new FakeExports { Attempt = null };

        var result = await Handler(exports, new FakeStorage(exports.Calls)).HandleAsync(Delivery(), CancellationToken.None);

        Assert.Equal(HandlerResult.Ack, result);
        Assert.Equal(["get", "start"], exports.Calls);
    }

    // ---- failures ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_expired_export_fails_for_good_after_its_attempt_started_and_nothing_is_built()
    {
        var exports = new FakeExports { TaskState = State(ProcessingTaskStatus.Pending, expired: true) };
        var storage = new FakeStorage(exports.Calls);

        var result = await Handler(exports, storage).HandleAsync(Delivery(), CancellationToken.None);

        var failure = Assert.IsType<HandlerResult.Fail>(result);
        Assert.Equal((1, ReportErrorCodes.ExportExpired), (failure.AttemptNo, failure.ErrorCode));
        // The attempt exists (the failure is recorded on it by the final-failure callback); no file.
        Assert.Equal(["get", "start"], exports.Calls);
        Assert.Empty(storage.Puts);
    }

    [Theory]
    [InlineData(ReportErrorCodes.ExportTooLarge)]
    [InlineData(ReportErrorCodes.ParametersInvalid)]
    public async Task An_export_that_cannot_be_built_fails_for_good_without_a_retry(string errorCode)
    {
        var exports = new FakeExports { GenerateFailure = new ReportExportRejectedException(errorCode, "cannot be built") };
        var storage = new FakeStorage(exports.Calls);

        var result = await Handler(exports, storage).HandleAsync(Delivery(), CancellationToken.None);

        Assert.Equal(new HandlerResult.Fail(1, errorCode, "cannot be built"), result);
        Assert.Equal(["get", "start", "generate"], exports.Calls);
        Assert.Empty(exports.TransientFailures);
    }

    [Fact]
    public async Task A_storage_failure_is_recorded_on_the_attempt_and_retried()
    {
        var exports = new FakeExports { Attempt = new AttemptStart(Guid.NewGuid(), 4, 3) };
        var storage = new FakeStorage(exports.Calls) { PutFailure = new ObjectStorageException("put") };

        var result = await Handler(exports, storage).HandleAsync(Delivery(), CancellationToken.None);

        // The attempt number and the budget of the task go back to the consumer: it decides between a retry and the end.
        var retry = Assert.IsType<HandlerResult.Retry>(result);
        Assert.Equal((4, ObjectStorageException.ErrorCode, 3, 1), (retry.AttemptNo, retry.ErrorCode, retry.AttemptBudgetStart, retry.AttemptInBudget));
        Assert.Equal(["get", "start", "generate", "put", "transient"], exports.Calls);
        var recorded = Assert.Single(exports.TransientFailures);
        Assert.Equal((TaskId, exports.Attempt.AttemptId, ObjectStorageException.ErrorCode), (recorded.TaskId, recorded.AttemptId, recorded.ErrorCode));
        // The message names the operation and nothing of the storage.
        Assert.Equal("ObjectStorageException: Object storage operation 'put' failed.", recorded.ErrorMessage);
        Assert.Empty(exports.Completed);
        Assert.False(exports.Generated!.Content.CanRead);
    }

    [Fact]
    public async Task A_temporary_database_error_is_retried_and_a_permanent_one_is_not()
    {
        var temporary = new FakeExports { GenerateFailure = new TimeoutException("slow") };
        var permanent = new FakeExports
        {
            CompleteFailure = new PostgresException("violates check constraint", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation)
        };

        var retried = await Handler(temporary, new FakeStorage(temporary.Calls)).HandleAsync(Delivery(), CancellationToken.None);
        var failed = await Handler(permanent, new FakeStorage(permanent.Calls)).HandleAsync(Delivery(), CancellationToken.None);

        Assert.Equal("timeout", Assert.IsType<HandlerResult.Retry>(retried).ErrorCode);
        Assert.Equal("timeout", Assert.Single(temporary.TransientFailures).ErrorCode);
        Assert.Equal("check_violation", Assert.IsType<HandlerResult.Fail>(failed).ErrorCode);
        Assert.Empty(permanent.TransientFailures);
    }

    [Fact]
    public async Task A_failure_that_cannot_be_recorded_is_still_retried()
    {
        var exports = new FakeExports { GenerateFailure = new TimeoutException("slow"), RecordFailure = new TimeoutException("database down") };

        var result = await Handler(exports, new FakeStorage(exports.Calls)).HandleAsync(Delivery(), CancellationToken.None);

        Assert.IsType<HandlerResult.Retry>(result);
    }

    // ---- shutdown ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_delivery_that_arrives_while_the_host_stops_is_left_alone()
    {
        var exports = new FakeExports();
        var lifetime = new FakeLifetime();
        lifetime.StopApplication();

        var result = await Handler(exports, new FakeStorage(exports.Calls), lifetime).HandleAsync(Delivery(), CancellationToken.None);

        Assert.Equal(HandlerResult.Abandon, result);
        Assert.Empty(exports.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Work_that_is_cancelled_because_the_host_stops_abandons_the_delivery_without_recording_a_failure(bool duringUpload)
    {
        var lifetime = new FakeLifetime();
        var exports = new FakeExports();
        var storage = new FakeStorage(exports.Calls);
        // Building and uploading run on the host's stopping token: they see the stop and give up.
        if (duringUpload)
        {
            storage.OnPut = lifetime.StopApplication;
        }
        else
        {
            exports.OnGenerate = lifetime.StopApplication;
        }

        var result = await Handler(exports, storage, lifetime).HandleAsync(Delivery(), CancellationToken.None);

        Assert.Equal(HandlerResult.Abandon, result);
        Assert.Empty(exports.TransientFailures);
        Assert.Empty(exports.Completed);
    }

    // ---- the last delivery the queue allows -------------------------------------------------------------------

    [Fact]
    public async Task The_final_failure_is_recorded_on_the_attempt_of_this_delivery()
    {
        var exports = new FakeExports { Attempt = new AttemptStart(Guid.NewGuid(), 5, 0) };
        var storage = new FakeStorage(exports.Calls) { PutFailure = new ObjectStorageException("put") };
        var handler = Handler(exports, storage);
        await handler.HandleAsync(Delivery(), CancellationToken.None);

        var toDlq = await handler.RecordFinalFailureAsync(
            Delivery(), new FinalFailure(5, ObjectStorageException.ErrorCode, "storage down", "report.export"), CancellationToken.None);

        Assert.True(toDlq);
        Assert.Equal(
            (TaskId, (Guid?)exports.Attempt.AttemptId, ObjectStorageException.ErrorCode, ReportExportMessageHandler.WorkerName, "report.export"),
            Assert.Single(exports.FinalFailures));
    }

    [Fact]
    public async Task Without_a_handled_delivery_the_final_failure_finds_the_task_in_the_message_and_a_finished_task_is_acknowledged()
    {
        var exports = new FakeExports();
        var finished = new FakeExports { FinalFailureResult = null };
        var failure = new FinalFailure(null, "delivery_limit_reached", "crash loop", "report.export");

        var toDlq = await Handler(exports, new FakeStorage(exports.Calls)).RecordFinalFailureAsync(Delivery(), failure, CancellationToken.None);
        var acknowledged = await Handler(finished, new FakeStorage(finished.Calls)).RecordFinalFailureAsync(Delivery(), failure, CancellationToken.None);

        Assert.True(toDlq);
        // No attempt of this delivery: the failure becomes an attempt of its own.
        Assert.Equal((TaskId, (Guid?)null, "delivery_limit_reached", ReportExportMessageHandler.WorkerName, "report.export"), Assert.Single(exports.FinalFailures));
        Assert.False(acknowledged);
    }

    // ---- helpers (also used by MessageRejectionTests) ------------------------------------------------------------

    internal static ReportExportTaskState State(
        ProcessingTaskStatus status = ProcessingTaskStatus.Pending,
        ProcessingTaskType type = ProcessingTaskType.ReportExport,
        bool hasExport = true,
        bool expired = false) =>
        new(TaskId, type, status, "corr-1", hasExport, expired);

    internal static ReportExportMessageHandler Handler(
        IReportExportProcessingService exports, IObjectStorage storage, IHostApplicationLifetime? lifetime = null) =>
        new(
            exports,
            storage,
            new WorkerCurrentUser(),
            new WorkerCorrelationContext(),
            Options.Create(new WorkerOptions()),
            lifetime ?? new FakeLifetime(),
            NullLogger<ReportExportMessageHandler>.Instance);

    internal static ReceivedMessage Delivery(byte[]? body = null) =>
        new(body ?? Envelope(TaskId), "amqp-message-id", "amqp-correlation-id", Redelivered: false, "report.export.requested", DeliveryCount: 0, new BasicProperties());

    internal static byte[] Envelope(Guid taskId) =>
        MessageJson.SerializeToUtf8Bytes(new MessageEnvelope(
            "msg-1", "corr-1", taskId, EventTypes.ReportExportRequested, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new ReportExportRequestedPayload(taskId), MessageJson.Options)));

    /// <summary>The database side of the worker: answers as configured and records what was called, in order.</summary>
    internal sealed class FakeExports : IReportExportProcessingService
    {
        public List<string> Calls { get; } = [];

        public ReportExportTaskState? TaskState { get; init; } = State();

        public AttemptStart? Attempt { get; init; } = new(Guid.NewGuid(), 1, 0);

        public Exception? GenerateFailure { get; init; }

        public Exception? CompleteFailure { get; init; }

        public Exception? RecordFailure { get; init; }

        public Action? OnGenerate { get; set; }

        public int? FinalFailureResult { get; init; } = 1;

        public GeneratedReport? Generated { get; private set; }

        public List<(Guid TaskId, Guid AttemptId, long SizeBytes, int RowCount)> Completed { get; } = [];

        public List<(Guid TaskId, Guid AttemptId, string ErrorCode, string ErrorMessage)> TransientFailures { get; } = [];

        public List<(Guid TaskId, Guid? AttemptId, string ErrorCode, string WorkerName, string Queue)> FinalFailures { get; } = [];

        public List<MessageRejection> Rejections { get; } = [];

        public Task<ReportExportTaskState?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
        {
            Calls.Add("get");
            return Task.FromResult(TaskState);
        }

        public Task<AttemptStart?> StartAttemptAsync(Guid taskId, string messageId, string workerName, string workerInstanceId, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            return Task.FromResult(Attempt);
        }

        public Task<GeneratedReport> GenerateAsync(Guid taskId, CancellationToken cancellationToken)
        {
            Calls.Add("generate");
            OnGenerate?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (GenerateFailure is not null)
            {
                throw GenerateFailure;
            }

            Generated = new GeneratedReport(new MemoryStream(new byte[2048]), "orders_20261005-1030.xlsx", 7);
            return Task.FromResult(Generated);
        }

        public Task CompleteAsync(Guid taskId, Guid attemptId, long sizeBytes, int rowCount, CancellationToken cancellationToken)
        {
            Calls.Add("complete");
            if (CompleteFailure is not null)
            {
                throw CompleteFailure;
            }

            Completed.Add((taskId, attemptId, sizeBytes, rowCount));
            return Task.CompletedTask;
        }

        public Task RecordTransientFailureAsync(Guid taskId, Guid attemptId, string errorCode, string errorMessage, CancellationToken cancellationToken)
        {
            Calls.Add("transient");
            if (RecordFailure is not null)
            {
                throw RecordFailure;
            }

            TransientFailures.Add((taskId, attemptId, errorCode, errorMessage));
            return Task.CompletedTask;
        }

        public Task<int?> RecordFinalFailureAsync(
            Guid taskId, Guid? attemptId, string errorCode, string errorMessage, string workerName, string workerInstanceId, string queue,
            CancellationToken cancellationToken)
        {
            FinalFailures.Add((taskId, attemptId, errorCode, workerName, queue));
            return Task.FromResult(FinalFailureResult);
        }

        public Task RecordRejectedMessageAsync(
            string? messageId, string? correlationId, Guid? taskId, MessageRejection rejection, string workerName, CancellationToken cancellationToken)
        {
            Rejections.Add(rejection);
            return Task.CompletedTask;
        }
    }

    /// <summary>The storage of the worker: records what is stored, into the same call list as the database side.</summary>
    internal sealed class FakeStorage(List<string> calls) : IObjectStorage
    {
        public List<(string Key, string ContentType, string ContentDisposition, long Length)> Puts { get; } = [];

        public Exception? PutFailure { get; init; }

        public Action? OnPut { get; set; }

        public Task PutAsync(string key, Stream content, string contentType, string contentDisposition, CancellationToken cancellationToken)
        {
            calls.Add("put");
            OnPut?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (PutFailure is not null)
            {
                throw PutFailure;
            }

            // A real upload reads the stream from its start to its end.
            Assert.Equal((true, 0L), (content.CanSeek, content.Position));
            Puts.Add((key, contentType, contentDisposition, content.Length));
            return Task.CompletedTask;
        }

        public PresignedUrl PresignPut(string key, string contentType, TimeSpan lifetime) => throw new NotSupportedException();

        public PresignedUrl PresignGet(string key, TimeSpan lifetime) => throw new NotSupportedException();

        public Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]> ReadStartAsync(string key, int count, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }

    /// <summary>A body that is not UTF-8 JSON at all.</summary>
    internal static byte[] NotJson => Encoding.UTF8.GetBytes("{ nope");
}
