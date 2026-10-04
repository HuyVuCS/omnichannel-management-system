using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers;
using Omnichannel.Workers.OrderWorker;
using Omnichannel.Workers.StockSyncWorker;
using RabbitMQ.Client;

namespace Omnichannel.Tests.Workers;

/// <summary>
/// Task 019 A2: every way a worker rejects a message writes its own <c>reasonCode</c> (<see cref="MessageRejectionCodes"/>)
/// - through both call sites of each handler: the normal delivery (<c>HandleAsync</c>) and the last delivery the queue
/// allows (<c>RecordFinalFailureAsync</c> before any attempt).
/// </summary>
public sealed class MessageRejectionTests
{
    private const string WebsiteQueue = "stock.sync.website";

    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid ChannelId = Guid.NewGuid();
    private static readonly Guid ProductId = Guid.NewGuid();

    // ---- order worker ------------------------------------------------------------------------------------------

    public static TheoryData<string, byte[]> OrderRejections()
    {
        var product = Guid.NewGuid();
        return new TheoryData<string, byte[]>
        {
            { MessageRejectionCodes.InvalidJson, Encoding.UTF8.GetBytes("{ nope") },
            { MessageRejectionCodes.EmptyBody, Encoding.UTF8.GetBytes("null") },
            { MessageRejectionCodes.UnexpectedEventType, Envelope(EventTypes.InventoryChanged, TaskId, Order()) },
            { MessageRejectionCodes.MissingTaskId, Envelope(EventTypes.OrderCreated, null, Order()) },
            { MessageRejectionCodes.MissingMessageIds, Envelope(EventTypes.OrderCreated, TaskId, Order(), messageId: " ") },
            { MessageRejectionCodes.MissingMessageIds, Envelope(EventTypes.OrderCreated, TaskId, Order(), correlationId: "") },
            { MessageRejectionCodes.PayloadNotObject, Envelope(EventTypes.OrderCreated, TaskId, new[] { 1, 2 }) },
            { MessageRejectionCodes.InvalidPayload, Envelope(EventTypes.OrderCreated, TaskId, new { taskId = "x" }) },
            { MessageRejectionCodes.TaskIdMismatch, Envelope(EventTypes.OrderCreated, TaskId, Order(taskId: Guid.NewGuid())) },
            { MessageRejectionCodes.MissingOrderFields, Envelope(EventTypes.OrderCreated, TaskId, Order(customerId: Guid.Empty)) },
            { MessageRejectionCodes.NoItems, Envelope(EventTypes.OrderCreated, TaskId, Order(items: [])) },
            { MessageRejectionCodes.InvalidItem, Envelope(EventTypes.OrderCreated, TaskId, Order(items: [new OrderCreatedItem(product, 0, 1m)])) },
            {
                MessageRejectionCodes.DuplicateProduct,
                Envelope(EventTypes.OrderCreated, TaskId, Order(items: [new OrderCreatedItem(product, 1, 1m), new OrderCreatedItem(product, 2, 1m)]))
            },
            // Well-formed, but the stub knows no task.
            { MessageRejectionCodes.UnknownTask, Envelope(EventTypes.OrderCreated, TaskId, Order()) }
        };
    }

    [Theory]
    [MemberData(nameof(OrderRejections))]
    public async Task The_order_worker_writes_the_code_of_the_rejection(string expectedCode, byte[] body)
    {
        var onDelivery = new RecordingReservations();
        var result = await OrderHandler(onDelivery).HandleAsync(Delivery(body), CancellationToken.None);

        var rejection = Assert.Single(onDelivery.Rejections);
        Assert.Equal(expectedCode, rejection.Code);
        Assert.False(string.IsNullOrWhiteSpace(rejection.Reason));
        // The text goes to the broker log line only; the code is what the Logs page shows.
        Assert.Equal(new HandlerResult.Reject(rejection.Reason), result);

        // The same message on the last delivery the queue allows (no HandleAsync before).
        var onLastDelivery = new RecordingReservations();
        var toDlq = await OrderHandler(onLastDelivery).RecordFinalFailureAsync(
            Delivery(body), new FinalFailure(null, "delivery_limit", "too many deliveries", "order.processing"), CancellationToken.None);

        Assert.True(toDlq);
        Assert.Equal(expectedCode, Assert.Single(onLastDelivery.Rejections).Code);
    }

    // ---- stock-sync worker -------------------------------------------------------------------------------------

    public static TheoryData<string, byte[], StockSyncTaskState?> StockSyncRejections()
    {
        var change = new InventoryChangedPayload(ProductId, Guid.NewGuid(), InventoryMovementType.Import, 1, 0, 1, DateTimeOffset.UtcNow);
        var request = new StockSyncRequestedPayload(TaskId, ChannelId, ProductId);
        var directed = Envelope(EventTypes.StockSyncRequested, TaskId, request);
        StockSyncTaskState Task(
            ProcessingTaskType type = ProcessingTaskType.StockSync, Guid? channelId = null, string slug = "website", Guid? productId = null) =>
            new(TaskId, type, ProcessingTaskStatus.Pending, "corr-1", channelId ?? ChannelId, slug, productId ?? ProductId);

        return new TheoryData<string, byte[], StockSyncTaskState?>
        {
            { MessageRejectionCodes.InvalidJson, Encoding.UTF8.GetBytes("{ not json"), null },
            { MessageRejectionCodes.EmptyBody, Encoding.UTF8.GetBytes("null"), null },
            { MessageRejectionCodes.UnexpectedEventType, Envelope(EventTypes.OrderCreated, TaskId, change), null },
            { MessageRejectionCodes.MissingMessageIds, Envelope(EventTypes.InventoryChanged, null, change, messageId: " "), null },
            { MessageRejectionCodes.MissingMessageIds, Envelope(EventTypes.InventoryChanged, null, change, correlationId: ""), null },
            { MessageRejectionCodes.MessageIdsTooLong, Envelope(EventTypes.InventoryChanged, null, change, messageId: new string('m', 101)), null },
            { MessageRejectionCodes.MessageIdsTooLong, Envelope(EventTypes.InventoryChanged, null, change, correlationId: new string('c', 101)), null },
            { MessageRejectionCodes.PayloadNotObject, Envelope(EventTypes.InventoryChanged, null, "text"), null },
            { MessageRejectionCodes.InvalidPayload, Envelope(EventTypes.InventoryChanged, null, new { productId = "x" }), null },
            { MessageRejectionCodes.MissingProductId, Envelope(EventTypes.InventoryChanged, null, new { productId = Guid.Empty }), null },
            { MessageRejectionCodes.MissingTaskId, Envelope(EventTypes.StockSyncRequested, null, request), null },
            { MessageRejectionCodes.InvalidPayload, Envelope(EventTypes.StockSyncRequested, TaskId, new { taskId = "x" }), null },
            { MessageRejectionCodes.TaskIdMismatch, Envelope(EventTypes.StockSyncRequested, TaskId, request with { TaskId = Guid.NewGuid() }), null },
            { MessageRejectionCodes.MissingSyncTarget, Envelope(EventTypes.StockSyncRequested, TaskId, request with { ChannelId = Guid.Empty }), null },
            { MessageRejectionCodes.MissingSyncTarget, Envelope(EventTypes.StockSyncRequested, TaskId, request with { ProductId = Guid.Empty }), null },
            // Well-formed; what the database says about the task decides.
            { MessageRejectionCodes.UnknownTask, directed, null },
            { MessageRejectionCodes.TaskMismatch, directed, Task(type: ProcessingTaskType.OrderProcessing) },
            { MessageRejectionCodes.TaskMismatch, directed, Task(productId: Guid.NewGuid()) },
            { MessageRejectionCodes.TaskMismatch, directed, Task(channelId: Guid.NewGuid()) },
            { MessageRejectionCodes.TaskOfOtherChannel, directed, Task(slug: "pos") }
        };
    }

    [Theory]
    [MemberData(nameof(StockSyncRejections))]
    public async Task The_stock_sync_worker_writes_the_code_of_the_rejection(string expectedCode, byte[] body, StockSyncTaskState? task)
    {
        var onDelivery = new RecordingStockSync(task);
        var result = await StockSyncHandler(onDelivery).HandleAsync(Delivery(body), CancellationToken.None);

        var (rejection, queue) = Assert.Single(onDelivery.Rejections);
        Assert.Equal((expectedCode, WebsiteQueue), (rejection.Code, queue));
        Assert.False(string.IsNullOrWhiteSpace(rejection.Reason));
        Assert.Equal(new HandlerResult.Reject(rejection.Reason), result);

        var onLastDelivery = new RecordingStockSync(task);
        var toDlq = await StockSyncHandler(onLastDelivery).RecordFinalFailureAsync(
            Delivery(body), new FinalFailure(null, "delivery_limit", "too many deliveries", WebsiteQueue), CancellationToken.None);

        Assert.True(toDlq);
        Assert.Equal(expectedCode, Assert.Single(onLastDelivery.Rejections).Rejection.Code);
    }

    // ---- the set of codes --------------------------------------------------------------------------------------

    [Fact]
    public void Every_code_is_written_by_a_path_above_except_the_one_no_message_can_reach()
    {
        var constants = typeof(MessageRejectionCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.Equal(constants.Order(StringComparer.Ordinal), MessageRejectionCodes.All.Order(StringComparer.Ordinal));
        Assert.All(constants, code => Assert.Matches("^[a-z]+(_[a-z]+)*$", code));

        var covered = OrderRejections().Select(row => (string)row[0])
            .Concat(StockSyncRejections().Select(row => (string)row[0]))
            .Distinct();

        // "Payload is empty": the parsers keep the check, but a payload that passed "is a JSON object" never deserialises
        // to null, so no message produces it.
        Assert.Equal(
            constants.Except([MessageRejectionCodes.EmptyPayload]).Order(StringComparer.Ordinal),
            covered.Order(StringComparer.Ordinal));
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static OrderCreatedPayload Order(Guid? taskId = null, Guid? customerId = null, OrderCreatedItem[]? items = null) =>
        new(
            taskId ?? TaskId,
            Guid.NewGuid(),
            "website",
            "EXT-1",
            customerId ?? Guid.NewGuid(),
            items ?? [new OrderCreatedItem(Guid.NewGuid(), 1, 9.99m)],
            DateTimeOffset.UtcNow,
            "user-1");

    private static byte[] Envelope(string eventType, Guid? taskId, object payload, string messageId = "msg-1", string correlationId = "corr-1") =>
        MessageJson.SerializeToUtf8Bytes(new MessageEnvelope(
            messageId, correlationId, taskId, eventType, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(payload, MessageJson.Options)));

    private static ReceivedMessage Delivery(byte[] body) =>
        new(body, "amqp-message-id", "amqp-correlation-id", Redelivered: false, "routing-key", DeliveryCount: 0, new BasicProperties());

    private static OrderMessageHandler OrderHandler(IOrderReservationService reservations) =>
        new(
            reservations,
            new WorkerCurrentUser(),
            new WorkerCorrelationContext(),
            Options.Create(new WorkerOptions()),
            NullLogger<OrderMessageHandler>.Instance);

    private static StockSyncMessageHandler StockSyncHandler(IStockSyncService stockSync) =>
        new(
            new StockSyncChannel("website", WebsiteQueue),
            stockSync,
            new NoChannel(),
            new WorkerCurrentUser(),
            new WorkerCorrelationContext(),
            Options.Create(new StockSyncWorkerOptions()),
            Options.Create(new WorkerOptions()),
            new RunningHost(),
            NullLogger<StockSyncMessageHandler>.Instance);

    /// <summary>Knows no task and records what is rejected; a rejected message must not reach anything else.</summary>
    private sealed class RecordingReservations : IOrderReservationService
    {
        public List<MessageRejection> Rejections { get; } = [];

        public Task<ProcessingTaskStatus?> GetTaskStatusAsync(Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult<ProcessingTaskStatus?>(null);

        public Task RecordRejectedMessageAsync(
            string? messageId, string? correlationId, Guid? taskId, MessageRejection rejection, string workerName, CancellationToken cancellationToken)
        {
            Rejections.Add(rejection);
            return Task.CompletedTask;
        }

        public Task<AttemptStart?> StartAttemptAsync(Guid taskId, string workerName, string workerInstanceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationResult> ReserveAsync(OrderCreatedPayload order, Guid attemptId, string workerName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordTransientFailureAsync(Guid taskId, Guid attemptId, string errorCode, string errorMessage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int?> RecordFinalFailureAsync(
            Guid taskId, Guid? attemptId, string errorCode, string errorMessage, string workerName, string workerInstanceId, string queue,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Answers a directed message with <paramref name="task"/> and records what is rejected.</summary>
    private sealed class RecordingStockSync(StockSyncTaskState? task) : IStockSyncService
    {
        public List<(MessageRejection Rejection, string Queue)> Rejections { get; } = [];

        public Task<StockSyncTaskState?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(task);

        public Task RecordRejectedMessageAsync(
            string? messageId, string? correlationId, Guid? taskId, MessageRejection rejection, string workerName, string queue,
            CancellationToken cancellationToken)
        {
            Rejections.Add((rejection, queue));
            return Task.CompletedTask;
        }

        public Task<StockSyncResolution> ResolveInventoryChangeAsync(
            string channelSlug, Guid productId, string messageId, string correlationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AttemptStart?> StartAttemptAsync(Guid taskId, string workerName, string workerInstanceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StockSyncTarget?> ReadTargetAsync(Guid channelId, Guid productId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteAsync(Guid taskId, Guid attemptId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RecordTransientFailureAsync(Guid taskId, Guid attemptId, string errorCode, string errorMessage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int?> RecordFinalFailureAsync(
            Guid taskId, Guid? attemptId, string errorCode, string errorMessage, string workerName, string workerInstanceId, string queue,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoChannel : IChannelStockClient
    {
        public Task PushAvailableStockAsync(ChannelStockUpdate update, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RunningHost : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
