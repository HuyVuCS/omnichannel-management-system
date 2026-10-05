using Omnichannel.Application.Reports;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Reports;

/// <summary>
/// Task 021 A2: from its <c>ExpiresAt</c> on a finished export is expired for the API, whether or not the retention
/// sweep has run - at the exact boundary (the API tests can only move <c>ExpiresAt</c> around the real clock).
/// </summary>
public sealed class ReportExportExpiryTests
{
    private static readonly DateTimeOffset ExpiresAt = new(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void The_retention_time_is_over_from_the_expiry_time_on(int secondsAfterExpiry, bool expected)
    {
        var now = ExpiresAt.AddSeconds(secondsAfterExpiry);

        Assert.Equal(expected, ReportExportExpiry.IsPastRetention(deletedAt: null, ExpiresAt, now));
    }

    [Fact]
    public void A_deleted_file_ends_the_retention_time_whatever_the_clock_says()
    {
        var now = ExpiresAt.AddDays(-3);

        Assert.True(ReportExportExpiry.IsPastRetention(deletedAt: now.AddMinutes(-1), ExpiresAt, now));
    }

    [Theory]
    // One second before the expiry time: the status of the task.
    [InlineData(ProcessingTaskStatus.Succeeded, -1, ReportExportStatus.Succeeded)]
    [InlineData(ProcessingTaskStatus.Failed, -1, ReportExportStatus.Failed)]
    // From the expiry time on a finished export is expired, also before any sweep.
    [InlineData(ProcessingTaskStatus.Succeeded, 0, ReportExportStatus.Expired)]
    [InlineData(ProcessingTaskStatus.Failed, 0, ReportExportStatus.Expired)]
    [InlineData(ProcessingTaskStatus.Succeeded, 3600, ReportExportStatus.Expired)]
    // An export that is not finished keeps the status of its task: the worker fails it as expired.
    [InlineData(ProcessingTaskStatus.Pending, 3600, ReportExportStatus.Pending)]
    [InlineData(ProcessingTaskStatus.InProgress, 3600, ReportExportStatus.InProgress)]
    [InlineData(ProcessingTaskStatus.RetryScheduled, 3600, ReportExportStatus.RetryScheduled)]
    public void The_status_is_the_tasks_until_a_finished_export_reaches_its_expiry_time(
        ProcessingTaskStatus taskStatus, int secondsAfterExpiry, ReportExportStatus expected)
    {
        var now = ExpiresAt.AddSeconds(secondsAfterExpiry);

        Assert.Equal(expected, ReportExportExpiry.StatusOf(taskStatus, deletedAt: null, ExpiresAt, now));
    }

    [Theory]
    [InlineData(ProcessingTaskStatus.Pending)]
    [InlineData(ProcessingTaskStatus.InProgress)]
    [InlineData(ProcessingTaskStatus.RetryScheduled)]
    [InlineData(ProcessingTaskStatus.Succeeded)]
    [InlineData(ProcessingTaskStatus.Failed)]
    public void An_export_whose_file_was_deleted_is_expired_whatever_its_task_says(ProcessingTaskStatus taskStatus)
    {
        var now = ExpiresAt.AddDays(-3);

        Assert.Equal(ReportExportStatus.Expired, ReportExportExpiry.StatusOf(taskStatus, deletedAt: now, ExpiresAt, now));
    }

    [Fact]
    public void Every_task_status_has_an_export_status_of_the_same_name()
    {
        var now = ExpiresAt.AddDays(-3);

        Assert.All(
            Enum.GetValues<ProcessingTaskStatus>(),
            taskStatus => Assert.Equal(taskStatus.ToString(), ReportExportExpiry.StatusOf(taskStatus, null, ExpiresAt, now).ToString()));
    }
}
