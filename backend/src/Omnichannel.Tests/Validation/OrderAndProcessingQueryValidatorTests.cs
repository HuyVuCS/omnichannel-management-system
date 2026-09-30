using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Validation;

public sealed class OrderAndProcessingQueryValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly OrderListQueryValidator _orders = new();
    private readonly ProcessingTaskListQueryValidator _tasks = new();

    [Fact]
    public void Order_filters_are_valid()
    {
        var query = new OrderListQuery
        {
            Channel = "website",
            Status = OrderStatus.Reserved,
            CustomerId = Guid.NewGuid(),
            From = Now.AddDays(-1),
            To = Now,
            Search = "50%"
        };

        Assert.True(_orders.Validate(query).IsValid);
    }

    [Theory]
    [InlineData("createdAt")]
    [InlineData("TOTALAMOUNT")]
    [InlineData("externalOrderId")]
    public void Order_sort_whitelist(string sortBy)
    {
        Assert.True(_orders.Validate(new OrderListQuery { SortBy = sortBy }).IsValid);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("status")]
    [InlineData("updatedAt")] // valid for tasks, not for orders
    public void Order_rejects_unknown_sort(string sortBy)
    {
        Assert.False(_orders.Validate(new OrderListQuery { SortBy = sortBy }).IsValid);
    }

    [Fact]
    public void Order_rejects_inverted_range_unknown_status_and_long_channel()
    {
        Assert.False(_orders.Validate(new OrderListQuery { From = Now, To = Now.AddSeconds(-1) }).IsValid);
        Assert.False(_orders.Validate(new OrderListQuery { Status = (OrderStatus)99 }).IsValid);
        Assert.False(_orders.Validate(new OrderListQuery { Channel = new string('c', 65) }).IsValid);
        Assert.True(_orders.Validate(new OrderListQuery { From = Now, To = Now }).IsValid);
    }

    [Fact]
    public void Task_filters_are_valid()
    {
        var query = new ProcessingTaskListQuery
        {
            TaskType = ProcessingTaskType.OrderProcessing,
            Status = ProcessingTaskStatus.Failed,
            OrderId = Guid.NewGuid(),
            CorrelationId = "corr-1",
            From = Now.AddHours(-1),
            To = Now,
            WorkerName = "OrderWorker",
            WorkerInstanceId = "host-1",
            ErrorCode = "timeout",
            AttemptNo = 2
        };

        Assert.True(_tasks.Validate(query).IsValid);
        Assert.True(query.HasAttemptFilter);
    }

    [Theory]
    [InlineData("createdAt")]
    [InlineData("updatedAt")]
    public void Task_sort_whitelist(string sortBy)
    {
        Assert.True(_tasks.Validate(new ProcessingTaskListQuery { SortBy = sortBy }).IsValid);
    }

    [Theory]
    [InlineData("totalAmount")]
    [InlineData("attemptNo")]
    public void Task_rejects_unknown_sort(string sortBy)
    {
        Assert.False(_tasks.Validate(new ProcessingTaskListQuery { SortBy = sortBy }).IsValid);
    }

    [Fact]
    public void Task_rejects_invalid_filters()
    {
        Assert.False(_tasks.Validate(new ProcessingTaskListQuery { AttemptNo = 0 }).IsValid);
        Assert.False(_tasks.Validate(new ProcessingTaskListQuery { Status = (ProcessingTaskStatus)42 }).IsValid);
        Assert.False(_tasks.Validate(new ProcessingTaskListQuery { TaskType = (ProcessingTaskType)42 }).IsValid);
        Assert.False(_tasks.Validate(new ProcessingTaskListQuery { ErrorCode = new string('e', 65) }).IsValid);
        Assert.False(_tasks.Validate(new ProcessingTaskListQuery { WorkerName = new string('w', 101) }).IsValid);
        Assert.False(_tasks.Validate(new ProcessingTaskListQuery { WorkerInstanceId = new string('i', 101) }).IsValid);
        Assert.False(_tasks.Validate(new ProcessingTaskListQuery { CorrelationId = new string('c', 101) }).IsValid);
        Assert.False(_tasks.Validate(new ProcessingTaskListQuery { From = Now, To = Now.AddTicks(-1) }).IsValid);
    }

    [Fact]
    public void Blank_attempt_filters_are_no_attempt_filter()
    {
        Assert.False(new ProcessingTaskListQuery { WorkerName = " ", ErrorCode = "" }.HasAttemptFilter);
        Assert.True(new ProcessingTaskListQuery { AttemptNo = 1 }.HasAttemptFilter);
    }
}
