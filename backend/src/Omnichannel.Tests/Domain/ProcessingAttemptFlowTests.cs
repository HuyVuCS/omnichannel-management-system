using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Domain;

public sealed class ProcessingAttemptFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_attempt_is_number_one_and_task_in_progress()
    {
        var task = NewTask();

        var attempt = task.StartAttempt("OrderWorker", "i-1", Now);

        Assert.Equal((1, ProcessingAttemptStatus.Running, ProcessingTaskStatus.InProgress), (attempt.AttemptNo, attempt.Status, task.Status));
    }

    [Fact]
    public void Redelivery_abandons_the_running_attempt()
    {
        var task = NewTask();
        var crashed = task.StartAttempt("OrderWorker", "i-1", Now);

        var next = task.StartAttempt("OrderWorker", "i-2", Now.AddMinutes(1));

        Assert.Equal(2, next.AttemptNo);
        Assert.True(crashed.IsAbandoned);
        Assert.Equal(Now.AddMinutes(1), crashed.FinishedAt);
    }

    [Fact]
    public void Success_completes_task_and_attempt()
    {
        var task = NewTask();
        var attempt = task.StartAttempt("OrderWorker", "i-1", Now);
        var orderId = Guid.NewGuid();

        task.Succeed(orderId, attempt.AttemptId, Now.AddSeconds(1));

        Assert.Equal((ProcessingTaskStatus.Succeeded, orderId), (task.Status, task.OrderId!.Value));
        Assert.Equal((ProcessingAttemptStatus.Succeeded, Now.AddSeconds(1)), (attempt.Status, attempt.FinishedAt!.Value));
        Assert.True(task.IsTerminal);
        Assert.Throws<InvalidOperationException>(() => task.StartAttempt("OrderWorker", "i-1", Now));
        Assert.Throws<InvalidOperationException>(() => task.Succeed(orderId, attempt.AttemptId, Now));
    }

    [Fact]
    public void Transient_failure_schedules_a_retry()
    {
        var task = NewTask();
        var attempt = task.StartAttempt("OrderWorker", "i-1", Now);

        task.ScheduleRetry(attempt.AttemptId, "deadlock", "PostgreSQL 40P01: deadlock detected", Now);

        Assert.Equal((ProcessingTaskStatus.RetryScheduled, 1), (task.Status, task.RetryCount));
        Assert.Equal((ProcessingAttemptStatus.Failed, "deadlock"), (attempt.Status, attempt.ErrorCode));
        Assert.Equal(2, task.StartAttempt("OrderWorker", "i-1", Now).AttemptNo);
    }

    [Fact]
    public void Failure_after_a_concurrent_completion_leaves_the_task_succeeded()
    {
        var task = NewTask();
        var loser = task.StartAttempt("OrderWorker", "i-1", Now);
        var winner = task.StartAttempt("OrderWorker", "i-2", Now); // abandons "loser" although it still runs
        task.Succeed(Guid.NewGuid(), winner.AttemptId, Now);

        task.ScheduleRetry(loser.AttemptId, "timeout", "late failure", Now);

        Assert.Equal((ProcessingTaskStatus.Succeeded, 0), (task.Status, task.RetryCount));
        Assert.Equal("timeout", loser.ErrorCode);
    }

    [Fact]
    public void Attempt_of_an_already_completed_task_is_closed_as_succeeded()
    {
        var task = NewTask();
        var first = task.StartAttempt("OrderWorker", "i-1", Now);
        var second = task.StartAttempt("OrderWorker", "i-2", Now);
        task.Succeed(Guid.NewGuid(), second.AttemptId, Now);

        task.CloseAttemptOfCompletedTask(first.AttemptId, Now);

        Assert.Equal(ProcessingAttemptStatus.Succeeded, first.Status);
        Assert.Null(first.ErrorCode);
    }

    [Fact]
    public void Finished_attempts_cannot_change_again()
    {
        var task = NewTask();
        var attempt = task.StartAttempt("OrderWorker", "i-1", Now);
        task.ScheduleRetry(attempt.AttemptId, "timeout", "slow", Now);

        Assert.Throws<InvalidOperationException>(() => task.ScheduleRetry(attempt.AttemptId, "timeout", "again", Now));
        Assert.Throws<InvalidOperationException>(() => task.Succeed(null, Guid.NewGuid(), Now));
    }

    [Fact]
    public void Manual_retry_of_a_failed_task_resets_the_budget_and_keeps_the_attempts()
    {
        var task = NewTask();
        for (var n = 0; n < 3; n++)
        {
            var failing = task.StartAttempt("OrderWorker", "i-1", Now);
            task.ScheduleRetry(failing.AttemptId, "timeout", "slow", Now);
        }

        var last = task.StartAttempt("OrderWorker", "i-1", Now);
        task.Fail(last.AttemptId, "timeout", "slow", Now);

        var previous = task.RequeueForManualRetry("new-message");

        Assert.Equal("msg", previous);
        Assert.Equal((ProcessingTaskStatus.Pending, "new-message", 4, 4), (task.Status, task.MessageId, task.AttemptBudgetStart, task.Attempts.Count));
        Assert.Equal(3, task.RetryCount);

        // Attempt numbers keep counting after the manual retry.
        Assert.Equal(5, task.StartAttempt("OrderWorker", "i-2", Now).AttemptNo);
    }

    [Theory]
    [InlineData(ProcessingTaskStatus.Pending)]
    [InlineData(ProcessingTaskStatus.InProgress)]
    [InlineData(ProcessingTaskStatus.RetryScheduled)]
    [InlineData(ProcessingTaskStatus.Succeeded)]
    public void Only_a_failed_task_can_be_retried_manually(ProcessingTaskStatus status)
    {
        var task = NewTask();
        if (status != ProcessingTaskStatus.Pending)
        {
            var attempt = task.StartAttempt("OrderWorker", "i-1", Now);
            if (status == ProcessingTaskStatus.RetryScheduled)
            {
                task.ScheduleRetry(attempt.AttemptId, "timeout", "slow", Now);
            }
            else if (status == ProcessingTaskStatus.Succeeded)
            {
                task.Succeed(null, attempt.AttemptId, Now);
            }
        }

        Assert.Equal(status, task.Status);
        Assert.Throws<InvalidOperationException>(() => task.RequeueForManualRetry("new-message"));
        Assert.Equal("msg", task.MessageId);
    }

    private static ProcessingTask NewTask() => new(ProcessingTaskType.OrderProcessing, null, "corr", "msg");
}
