using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Domain;

public sealed class ProcessingTaskTests
{
    [Fact]
    public void Order_idempotency_key_has_the_documented_format()
    {
        var channelId = new Guid("0199a1b0-0000-7000-8000-000000000002");

        Assert.Equal("order:0199a1b0000070008000000000000002:WEB-1001", OrderIdempotencyKey.For(channelId, "WEB-1001"));
    }

    [Fact]
    public void Longest_order_key_fits_the_column()
    {
        var key = OrderIdempotencyKey.For(Guid.NewGuid(), new string('x', SubmitOrderRequestValidator.MaxExternalOrderIdLength));

        Assert.True(key.Length <= ProcessingTask.MaxIdempotencyKeyLength);
        Assert.Equal(key, new ProcessingTask(ProcessingTaskType.OrderProcessing, null, "corr", "msg", key).IdempotencyKey);
    }

    [Fact]
    public void New_task_is_pending_and_key_is_optional()
    {
        var task = new ProcessingTask(ProcessingTaskType.StockSync, null, "corr", null);

        Assert.Equal((ProcessingTaskStatus.Pending, 0, null), (task.Status, task.RetryCount, task.IdempotencyKey));
    }

    [Fact]
    public void Rejects_blank_or_oversized_key()
    {
        Assert.Throws<ArgumentException>(() => new ProcessingTask(ProcessingTaskType.OrderProcessing, null, "corr", null, " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessingTask(
            ProcessingTaskType.OrderProcessing, null, "corr", null, new string('k', ProcessingTask.MaxIdempotencyKeyLength + 1)));
    }

    [Fact]
    public void Outbox_event_can_use_a_pre_generated_id()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(id, new OutboxEvent("order.created", "agg", "{}", "corr", null, id).OutboxEventId);
        Assert.NotEqual(Guid.Empty, new OutboxEvent("order.created", "agg", "{}", "corr", null).OutboxEventId);
        Assert.Throws<ArgumentException>(() => new OutboxEvent("order.created", "agg", "{}", "corr", null, Guid.Empty));
    }
}
