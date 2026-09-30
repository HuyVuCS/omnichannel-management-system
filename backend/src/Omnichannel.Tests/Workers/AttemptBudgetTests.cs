using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Tests.Workers;

/// <summary>Task 008: a manual retry resets the attempt budget while attempt numbers keep increasing.</summary>
public sealed class AttemptBudgetTests
{
    private static readonly RetryPolicy Policy = new(MaxAttempts: 3, DeliveryLimit: 10);

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(3, 0, 3)]
    [InlineData(4, 3, 1)] // first attempt after a manual retry at attempt 3
    [InlineData(6, 3, 3)]
    [InlineData(7, 6, 1)] // second manual retry
    public void Attempt_in_budget_counts_from_the_last_manual_retry(int attemptNo, int budgetStart, int expected)
    {
        Assert.Equal(expected, RetryPolicy.AttemptInBudget(attemptNo, budgetStart));
        Assert.Equal(expected, new HandlerResult.Retry(attemptNo, "timeout", "x", budgetStart).AttemptInBudget);
    }

    [Fact]
    public void Attempt_numbered_before_the_budget_start_never_unlocks_more_retries()
    {
        Assert.Equal(1, RetryPolicy.AttemptInBudget(2, 5));
    }

    [Fact]
    public void Rejects_invalid_numbers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.AttemptInBudget(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.AttemptInBudget(1, -1));
    }

    [Fact]
    public void Retry_without_budget_start_behaves_as_before()
    {
        var retry = new HandlerResult.Retry(2, "timeout", "x");

        Assert.Equal((0, 2), (retry.AttemptBudgetStart, retry.AttemptInBudget));
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(2, 0, true)]
    [InlineData(3, 0, false)] // MaxAttempts reached
    [InlineData(4, 3, true)]  // fresh budget after the manual retry
    [InlineData(5, 3, true)]
    [InlineData(6, 3, false)] // MaxAttempts new attempts, then final again
    public void Limit_applies_to_the_attempts_since_the_last_manual_retry(int attemptNo, int budgetStart, bool canRetry)
    {
        Assert.Equal(canRetry, Policy.CanRetry(new HandlerResult.Retry(attemptNo, "timeout", "x", budgetStart).AttemptInBudget));
    }
}
