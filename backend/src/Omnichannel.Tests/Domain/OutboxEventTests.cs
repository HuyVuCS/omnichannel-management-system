using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Domain;

public sealed class OutboxEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Failure_below_the_limit_schedules_a_retry()
    {
        var row = NewRow();

        var failed = row.RecordPublishFailure("boom", Now, TimeSpan.FromSeconds(2), maxAttempts: 3);

        Assert.False(failed);
        Assert.Equal((OutboxEventStatus.Pending, 1, "boom"), (row.Status, row.PublishAttempts, row.LastError));
        Assert.Equal(Now.AddSeconds(2), row.NextAttemptAt);
    }

    [Fact]
    public void Failure_at_the_limit_is_terminal()
    {
        var row = NewRow();
        row.RecordPublishFailure("first", Now, TimeSpan.FromSeconds(1), maxAttempts: 2);

        var failed = row.RecordPublishFailure("second", Now, TimeSpan.FromSeconds(1), maxAttempts: 2);

        Assert.True(failed);
        Assert.Equal((OutboxEventStatus.Failed, 2, "second"), (row.Status, row.PublishAttempts, row.LastError));
        Assert.Null(row.NextAttemptAt);
        Assert.Throws<InvalidOperationException>(() => row.MarkPublished(Now));
    }

    [Fact]
    public void Publish_clears_the_retry_time_and_only_happens_once()
    {
        var row = NewRow();
        row.RecordPublishFailure("boom", Now, TimeSpan.FromSeconds(1), maxAttempts: 5);

        row.MarkPublished(Now);

        Assert.Equal((OutboxEventStatus.Published, Now, (DateTimeOffset?)null), (row.Status, row.PublishedAt, row.NextAttemptAt));
        Assert.Throws<InvalidOperationException>(() => row.MarkPublished(Now));
        Assert.Throws<InvalidOperationException>(() => row.RecordPublishFailure("late", Now, TimeSpan.Zero, 5));
    }

    [Fact]
    public void Long_errors_are_truncated()
    {
        var row = NewRow();

        row.RecordPublishFailure(new string('e', 5000), Now, TimeSpan.Zero, maxAttempts: 5);

        Assert.Equal(OutboxEvent.MaxLastErrorLength, row.LastError!.Length);
    }

    private static OutboxEvent NewRow() => new("order.created", Guid.NewGuid().ToString(), "{}", "corr", null);
}
