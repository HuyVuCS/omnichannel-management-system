using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 008: task search, task detail and manual-retry rules (the retry end to end with a worker is in
/// <see cref="ManualRetryEndToEndTests"/>). Tasks are seeded directly; each test filters by its own correlation id.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProcessingTaskEndpointsTests(ApiFactory factory)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _staff = factory.CreateClientAs(AppRoles.Staff);
    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);

    [Fact]
    public async Task Task_search_filters_on_task_and_attempt_fields()
    {
        var correlationId = $"tasks-{TestData.UniqueSuffix()}";

        // A: failed after two timeouts on two instances.
        var a = NewTask(ProcessingTaskType.OrderProcessing, correlationId);
        FailAttempt(a, "OrderWorker", "inst-1", "timeout");
        FinalFail(a, "OrderWorker", "inst-2", "timeout");
        // B: deadlock, then success.
        var b = NewTask(ProcessingTaskType.OrderProcessing, correlationId);
        FailAttempt(b, "OrderWorker", "inst-1", "deadlock");
        b.Succeed(null, b.StartAttempt("OrderWorker", "inst-2", T0).AttemptId, T0);
        // C: not started yet.
        var c = NewTask(ProcessingTaskType.OrderProcessing, correlationId);
        // D: an invoice failed permanently on another worker.
        var d = NewTask(ProcessingTaskType.Invoice, correlationId);
        FinalFail(d, "InvoiceWorker", "inst-3", "check_violation");
        await SaveAsync((a, T0.AddMinutes(1)), (b, T0.AddMinutes(2)), (c, T0.AddMinutes(3)), (d, T0.AddMinutes(4)));

        var all = await ListAsync($"correlationId={correlationId}");
        Assert.Equal([d.TaskId, c.TaskId, b.TaskId, a.TaskId], all.Items.Select(t => t.TaskId));
        Assert.Equal(4, all.TotalCount);

        var shownA = all.Items.Single(t => t.TaskId == a.TaskId);
        Assert.Equal((ProcessingTaskStatus.Failed, 2, correlationId, a.MessageId), (shownA.Status, shownA.AttemptCount, shownA.CorrelationId, shownA.MessageId));
        Assert.Equal((2, "inst-2", "timeout"), (shownA.LastAttempt!.AttemptNo, shownA.LastAttempt.WorkerInstanceId, shownA.LastAttempt.ErrorCode));
        var shownC = all.Items.Single(t => t.TaskId == c.TaskId);
        Assert.Equal((0, (ProcessingAttemptResponse?)null), (shownC.AttemptCount, shownC.LastAttempt));

        Assert.Equal([d.TaskId, a.TaskId], await TaskIdsAsync($"correlationId={correlationId}&status=Failed"));
        Assert.Equal([a.TaskId], await TaskIdsAsync($"correlationId={correlationId}&errorCode=timeout"));
        Assert.Equal([b.TaskId, a.TaskId], await TaskIdsAsync($"correlationId={correlationId}&workerName=OrderWorker"));
        Assert.Equal([b.TaskId, a.TaskId], await TaskIdsAsync($"correlationId={correlationId}&attemptNo=2"));
        Assert.Equal([d.TaskId], await TaskIdsAsync($"correlationId={correlationId}&workerInstanceId=inst-3"));
        Assert.Equal([d.TaskId], await TaskIdsAsync($"correlationId={correlationId}&taskType=Invoice"));

        // Attempt filters apply to one and the same attempt: only A has an attempt on inst-2 that timed out.
        Assert.Equal([a.TaskId], await TaskIdsAsync($"correlationId={correlationId}&workerInstanceId=inst-2&errorCode=timeout"));
        Assert.Empty(await TaskIdsAsync($"correlationId={correlationId}&workerName=InvoiceWorker&errorCode=timeout"));

        // Range and ascending order.
        var from = Uri.EscapeDataString(T0.AddMinutes(2).ToString("O"));
        var to = Uri.EscapeDataString(T0.AddMinutes(3).ToString("O"));
        Assert.Equal([c.TaskId, b.TaskId], await TaskIdsAsync($"correlationId={correlationId}&from={from}&to={to}"));
        Assert.Equal([a.TaskId, b.TaskId, c.TaskId, d.TaskId], await TaskIdsAsync($"correlationId={correlationId}&sortDir=asc"));
    }

    [Fact]
    public async Task The_last_attempt_of_every_task_of_a_page_is_its_highest_attempt_however_many_there_are()
    {
        var correlationId = $"last-{TestData.UniqueSuffix()}";
        // Five attempts, the last one succeeded on another instance than the four before.
        var five = NewTask(ProcessingTaskType.OrderProcessing, correlationId);
        foreach (var n in Enumerable.Range(1, 4))
        {
            FailAttempt(five, "OrderWorker", $"inst-{n}", "timeout");
        }

        five.Succeed(null, five.StartAttempt("OrderWorker", "inst-5", T0.AddHours(1)).AttemptId, T0.AddHours(2));
        // Three attempts, the last one failed for good with another error than the two before.
        var three = NewTask(ProcessingTaskType.Invoice, correlationId);
        FailAttempt(three, "InvoiceWorker", "inst-a", "timeout");
        FailAttempt(three, "InvoiceWorker", "inst-a", "deadlock");
        FinalFail(three, "InvoiceWorker", "inst-b", "check_violation");
        // One attempt, still running; and a task that was never started.
        var one = NewTask(ProcessingTaskType.OrderProcessing, correlationId);
        one.StartAttempt("OrderWorker", "inst-9", T0);
        var none = NewTask(ProcessingTaskType.OrderProcessing, correlationId);
        await SaveAsync((five, T0.AddMinutes(1)), (three, T0.AddMinutes(2)), (one, T0.AddMinutes(3)), (none, T0.AddMinutes(4)));

        var page = await ListAsync($"correlationId={correlationId}");

        Assert.Equal([none.TaskId, one.TaskId, three.TaskId, five.TaskId], page.Items.Select(t => t.TaskId));
        Assert.Equal([0, 1, 3, 5], page.Items.Select(t => t.AttemptCount));
        Assert.Null(page.Items[0].LastAttempt);
        Assert.Equal(
            new ProcessingAttemptResponse(1, ProcessingAttemptStatus.Running, "OrderWorker", "inst-9", null, null, T0, null),
            page.Items[1].LastAttempt);
        Assert.Equal(
            new ProcessingAttemptResponse(3, ProcessingAttemptStatus.Failed, "InvoiceWorker", "inst-b", "check_violation", "check_violation (seeded)", T0, T0),
            page.Items[2].LastAttempt);
        Assert.Equal(
            new ProcessingAttemptResponse(5, ProcessingAttemptStatus.Succeeded, "OrderWorker", "inst-5", null, null, T0.AddHours(1), T0.AddHours(2)),
            page.Items[3].LastAttempt);
        // The list says what the detail says.
        foreach (var task in page.Items.Where(t => t.LastAttempt is not null))
        {
            Assert.Equal((await GetAsync(task.TaskId)).Attempts[^1], task.LastAttempt);
        }
    }

    [Theory]
    [InlineData("/api/processing/tasks?sortBy=totalAmount")]
    [InlineData("/api/processing/tasks?attemptNo=0")]
    [InlineData("/api/processing/tasks?status=Exploded")]
    public async Task Invalid_task_search_parameters_return_validation_problem(string url)
    {
        var response = await _staff.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Task_detail_shows_attempts_and_the_publish_state_of_its_message()
    {
        var correlationId = $"detail-{TestData.UniqueSuffix()}";
        var (task, outbox) = NewOrderTask(correlationId);
        FailAttempt(task, "OrderWorker", "inst-1", "timeout");
        outbox.RecordPublishFailure("broker unreachable", T0, TimeSpan.FromSeconds(1), maxAttempts: 10);
        await SaveAsync(outbox, (task, T0));

        var pending = await GetAsync(task.TaskId);
        Assert.Equal((task.TaskId, ProcessingTaskStatus.RetryScheduled, correlationId, 0), (pending.TaskId, pending.Status, pending.CorrelationId, pending.AttemptBudgetStart));
        Assert.Equal((1, "timeout"), (Assert.Single(pending.Attempts).AttemptNo, pending.Attempts[0].ErrorCode));
        Assert.Equal((outbox.OutboxEventId, EventTypes.OrderCreated, OutboxEventStatus.Pending, 1, "broker unreachable", (DateTimeOffset?)null),
            (pending.Message!.OutboxEventId, pending.Message.EventType, pending.Message.Status, pending.Message.PublishAttempts,
                pending.Message.LastError, pending.Message.PublishedAt));

        var publishedAt = T0.AddSeconds(5);
        await factory.QueryDbAsync(async db =>
        {
            var row = await db.OutboxEvents.SingleAsync(e => e.OutboxEventId == outbox.OutboxEventId);
            row.MarkPublished(publishedAt);
            return await db.SaveChangesAsync();
        });

        var published = (await GetAsync(task.TaskId)).Message!;
        Assert.Equal((OutboxEventStatus.Published, publishedAt), (published.Status, published.PublishedAt!.Value));
    }

    [Fact]
    public async Task Task_without_an_outbox_row_has_no_message_and_unknown_task_is_404()
    {
        var task = NewTask(ProcessingTaskType.OrderProcessing, $"nomsg-{TestData.UniqueSuffix()}");
        await SaveAsync((task, T0));

        Assert.Null((await GetAsync(task.TaskId)).Message);

        var unknown = await _staff.GetAsync($"/api/processing/tasks/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(ProcessingErrorCodes.TaskNotFound, await unknown.ReadErrorCodeAsync());
    }

    [Theory]
    [InlineData(ProcessingTaskStatus.Pending)]
    [InlineData(ProcessingTaskStatus.RetryScheduled)]
    [InlineData(ProcessingTaskStatus.Succeeded)]
    public async Task Retry_of_a_task_that_is_not_failed_is_409(ProcessingTaskStatus status)
    {
        var (task, outbox) = NewOrderTask($"notfailed-{TestData.UniqueSuffix()}");
        if (status != ProcessingTaskStatus.Pending)
        {
            var attempt = task.StartAttempt("OrderWorker", "inst-1", T0);
            if (status == ProcessingTaskStatus.RetryScheduled)
            {
                task.ScheduleRetry(attempt.AttemptId, "timeout", "slow", T0);
            }
            else
            {
                task.Succeed(null, attempt.AttemptId, T0);
            }
        }

        await SaveAsync(outbox, (task, T0));

        var response = await _manager.PostAsync($"/api/processing/tasks/{task.TaskId}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ProcessingErrorCodes.TaskNotFailed, await response.ReadErrorCodeAsync());
        Assert.Equal(1, await OutboxRowsAsync(task.TaskId));
    }

    [Fact]
    public async Task Concurrent_retries_of_one_failed_task_accept_exactly_one()
    {
        var (task, outbox) = NewOrderTask($"race-{TestData.UniqueSuffix()}");
        FinalFail(task, "OrderWorker", "inst-1", "timeout");
        await SaveAsync(outbox, (task, T0));

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            factory.CreateClientAs(AppRoles.Manager).PostAsync($"/api/processing/tasks/{task.TaskId}/retry", null)));

        Assert.Equal(
            [HttpStatusCode.Accepted, HttpStatusCode.Conflict],
            responses.Select(r => r.StatusCode).OrderBy(code => (int)code));
        Assert.Equal(ProcessingErrorCodes.TaskNotFailed, await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).ReadErrorCodeAsync());
        Assert.Equal(2, await OutboxRowsAsync(task.TaskId)); // original + exactly one copy
        Assert.Single(await RetryEventsAsync(task.TaskId));

        var accepted = await responses.Single(r => r.StatusCode == HttpStatusCode.Accepted).ReadAsync<RetryProcessingTaskResponse>();
        var detail = await GetAsync(task.TaskId);
        Assert.Equal((ProcessingTaskStatus.Pending, accepted.MessageId, 1), (detail.Status, detail.MessageId, detail.AttemptBudgetStart));
        Assert.Equal((OutboxEventStatus.Pending, EventTypes.OrderCreated), (detail.Message!.Status, detail.Message.EventType));
    }

    [Fact]
    public async Task Retry_copies_the_original_message_and_records_who_asked()
    {
        var correlationId = $"copy-{TestData.UniqueSuffix()}";
        var (task, outbox) = NewOrderTask(correlationId);
        FinalFail(task, "OrderWorker", "inst-1", "timeout");
        await SaveAsync(outbox, (task, T0));

        var response = await _manager.PostAsync($"/api/processing/tasks/{task.TaskId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.ReadAsync<RetryProcessingTaskResponse>();
        Assert.Equal((task.TaskId, ProcessingTaskStatus.Pending), (body.TaskId, body.Status));
        Assert.NotEqual(outbox.OutboxEventId.ToString(), body.MessageId);

        var copy = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.OutboxEventId == Guid.Parse(body.MessageId)));
        Assert.Equal((outbox.EventType, outbox.AggregateId, correlationId, OutboxEventStatus.Pending, (Guid?)null),
            (copy.EventType, copy.AggregateId, copy.CorrelationId, copy.Status, copy.InventoryMovementId));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(outbox.Payload), JsonNode.Parse(copy.Payload)), copy.Payload); // jsonb reorders keys

        var logged = Assert.Single(await RetryEventsAsync(task.TaskId));
        var payload = JsonNode.Parse(logged.Payload!)!;
        Assert.Equal((SystemEventLevel.Information, correlationId), (logged.Level, logged.CorrelationId));
        Assert.Equal(("OrderProcessing", outbox.OutboxEventId.ToString(), body.MessageId, 1),
            (payload["taskType"]!.GetValue<string>(), payload["previousMessageId"]!.GetValue<string>(),
                payload["newMessageId"]!.GetValue<string>(), payload["attemptBudgetStart"]!.GetValue<int>()));
        Assert.False(string.IsNullOrEmpty(payload["requestedBy"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Retry_without_the_original_outbox_row_is_409_and_unknown_task_is_404()
    {
        var task = NewTask(ProcessingTaskType.OrderProcessing, $"nosource-{TestData.UniqueSuffix()}");
        FinalFail(task, "OrderWorker", "inst-1", "timeout");
        await SaveAsync((task, T0));

        var missing = await _manager.PostAsync($"/api/processing/tasks/{task.TaskId}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal(ProcessingErrorCodes.RetrySourceMissing, await missing.ReadErrorCodeAsync());
        Assert.Equal(ProcessingTaskStatus.Failed, (await GetAsync(task.TaskId)).Status);

        var unknown = await _manager.PostAsync($"/api/processing/tasks/{Guid.NewGuid()}/retry", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(ProcessingErrorCodes.TaskNotFound, await unknown.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Staff_cannot_retry_but_can_read()
    {
        var (task, outbox) = NewOrderTask($"rbac-{TestData.UniqueSuffix()}");
        FinalFail(task, "OrderWorker", "inst-1", "timeout");
        await SaveAsync(outbox, (task, T0));

        var retry = await _staff.PostAsync($"/api/processing/tasks/{task.TaskId}/retry", null);

        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
        Assert.Equal(ProcessingTaskStatus.Failed, (await GetAsync(task.TaskId)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/processing/tasks")).StatusCode);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static ProcessingTask NewTask(ProcessingTaskType type, string correlationId) =>
        new(type, null, correlationId, messageId: null);

    /// <summary>An order task and the outbox row carrying its message (payload not processable: these tasks are never run).</summary>
    private static (ProcessingTask Task, OutboxEvent Outbox) NewOrderTask(string correlationId)
    {
        var outboxId = Guid.CreateVersion7();
        var task = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, correlationId, outboxId.ToString());
        var outbox = new OutboxEvent(
            EventTypes.OrderCreated,
            task.TaskId.ToString(),
            """{"seeded":true,"note":"test row"}""",
            correlationId,
            inventoryMovementId: null,
            outboxId);
        return (task, outbox);
    }

    private static void FailAttempt(ProcessingTask task, string worker, string instance, string errorCode) =>
        task.ScheduleRetry(task.StartAttempt(worker, instance, T0).AttemptId, errorCode, $"{errorCode} (seeded)", T0);

    private static void FinalFail(ProcessingTask task, string worker, string instance, string errorCode) =>
        task.Fail(task.StartAttempt(worker, instance, T0).AttemptId, errorCode, $"{errorCode} (seeded)", T0);

    private Task SaveAsync(params (ProcessingTask Task, DateTimeOffset CreatedAt)[] tasks) => SaveAsync(null, tasks);

    private Task SaveAsync(OutboxEvent? outbox, params (ProcessingTask Task, DateTimeOffset CreatedAt)[] tasks) =>
        factory.QueryDbAsync(async db =>
        {
            db.ProcessingTasks.AddRange(tasks.Select(t => t.Task));
            if (outbox is not null)
            {
                db.OutboxEvents.Add(outbox);
            }

            await db.SaveChangesAsync();
            foreach (var (task, createdAt) in tasks)
            {
                await db.Database.ExecuteSqlAsync(
                    $"""UPDATE "ProcessingTasks" SET "CreatedAt" = {createdAt} WHERE "TaskId" = {task.TaskId}""");
            }

            return 0;
        });

    private async Task<PagedResult<ProcessingTaskSummaryResponse>> ListAsync(string query)
    {
        var response = await _staff.GetAsync($"/api/processing/tasks?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<PagedResult<ProcessingTaskSummaryResponse>>();
    }

    private async Task<List<Guid>> TaskIdsAsync(string query) => (await ListAsync(query)).Items.Select(t => t.TaskId).ToList();

    private async Task<ProcessingTaskDetailResponse> GetAsync(Guid taskId)
    {
        var response = await _staff.GetAsync($"/api/processing/tasks/{taskId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<ProcessingTaskDetailResponse>();
    }

    private Task<int> OutboxRowsAsync(Guid taskId) =>
        factory.QueryDbAsync(db => db.OutboxEvents.CountAsync(e => e.AggregateId == taskId.ToString()));

    private Task<List<SystemEventLog>> RetryEventsAsync(Guid taskId) =>
        factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .Where(l => l.EventType == ProcessingEvents.RetryRequested && l.TaskId == taskId)
            .ToListAsync());
}
