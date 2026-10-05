using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Realtime;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;
using static Omnichannel.Tests.Integration.ReportExportTesting;

namespace Omnichannel.Tests.Integration;

// Task 021 A1: the alert of a failed export ends when the retention sweep expires the export - in one transaction.
public sealed partial class ReportExportEndToEndTests
{
    [Fact]
    public async Task The_sweep_resolves_the_alert_of_a_failed_export_it_expires_and_says_so()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var taskId = await FailedExportWithFileAsync(manager, "export-e2e-alert-end");
        var alert = await AlertOfAsync(taskId);
        Assert.Equal((AlertType.ProcessingFailed, AlertStatus.Open, ReportErrorCodes.ExportTooLarge), (alert.Type, alert.Status, alert.ErrorCode));
        await ExpireAsync(factory, taskId, TimeSpan.FromSeconds(30));

        using var host = await BuildWorkersAsync(factory, broker.HostName, broker.Port, "export-e2e-alert-end-run");
        await using var probe = await HintProbe.StartAsync(factory.DatabaseConnectionString);
        var before = DateTimeOffset.UtcNow;

        // One round.
        var result = await SweepAsync(host);

        Assert.True(result.Expired >= 1, $"expired: {result.Expired}");
        Assert.Equal(0, result.Failed);
        Assert.False(await _storage.ExistsAsync(ReportExport.ObjectKeyOf(taskId)));
        var deletedAt = (await ExportAsync(factory, taskId)).DeletedAt;
        Assert.InRange(deletedAt!.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        // Resolved by the system, at the moment of the mark; nothing else of the alert changed.
        var resolved = await AlertOfAsync(taskId);
        Assert.Equal(
            (alert.AlertId, AlertStatus.Resolved, alert.OccurrenceCount, alert.ErrorCode, alert.AcknowledgedAt),
            (resolved.AlertId, resolved.Status, resolved.OccurrenceCount, resolved.ErrorCode, resolved.AcknowledgedAt));
        Assert.InRange(resolved.ResolvedAt!.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        // alert.changed, as for every other resolution.
        await probe.WaitForAsync(all => all.Any(n => n.Events.Any(e => e.AlertId == alert.AlertId)), "alert.changed of the resolved alert");
        Assert.Equal(
            [RealtimeHint.AlertChanged(alert.AlertId, AlertType.ProcessingFailed, AlertStatus.Resolved, productId: null, channelId: null)],
            probe.Events.Where(e => e.AlertId == alert.AlertId));
        var detail = await DetailAsync(manager, taskId);
        Assert.Equal((ReportExportStatus.Expired, (string?)null), (detail.Status, detail.ErrorCode));

        // A later round finds nothing of it any more: the same mark, the same resolution, no second hint.
        await SweepAsync(host);
        Assert.Equal(deletedAt, (await ExportAsync(factory, taskId)).DeletedAt);
        Assert.Equal(resolved.ResolvedAt, (await AlertOfAsync(taskId)).ResolvedAt);
        Assert.Single(probe.Events, e => e.AlertId == alert.AlertId);
        // One alert of the task in all: the resolution opened no new one.
        Assert.Equal(1, await factory.QueryDbAsync(db => db.Alerts.CountAsync(a => a.DedupKey == AlertDedupKey.ProcessingFailed(taskId))));
    }

    [Fact]
    public async Task A_sweep_that_fails_before_its_commit_stores_neither_the_mark_nor_the_resolution()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var taskId = await FailedExportWithFileAsync(manager, "export-e2e-alert-rollback");
        var alert = await AlertOfAsync(taskId);

        // The same seam as the API host's: the next commit of this Workers host throws instead of committing.
        var commits = new CommitFailureInjection();
        using var host = await BuildWorkersAsync(
            factory,
            broker.HostName,
            broker.Port,
            "export-e2e-alert-rollback-run",
            configureServices: services => services.ConfigureDbContext<OmnichannelDbContext>(options => options.AddInterceptors(commits)));
        // What other tests left to expire is out of the way first: the next commit of a round is this export's.
        await SweepAsync(host);
        await ExpireAsync(factory, taskId, TimeSpan.FromSeconds(30));
        await using var probe = await HintProbe.StartAsync(factory.DatabaseConnectionString);

        commits.FailNextCommit();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => SweepAsync(host));

        Assert.Equal("injected failure before the commit", failure.Message);
        // The file was deleted before the transaction; the database shows nothing of the round.
        Assert.False(await _storage.ExistsAsync(ReportExport.ObjectKeyOf(taskId)));
        Assert.Null((await ExportAsync(factory, taskId)).DeletedAt);
        var unchanged = await AlertOfAsync(taskId);
        Assert.Equal((alert.AlertId, AlertStatus.Open, (DateTimeOffset?)null), (unchanged.AlertId, unchanged.Status, unchanged.ResolvedAt));

        // The next round finishes both (deleting a key without an object succeeds).
        var result = await SweepAsync(host);

        Assert.Equal((1, 0), (result.Expired, result.Failed));
        Assert.NotNull((await ExportAsync(factory, taskId)).DeletedAt);
        Assert.Equal(AlertStatus.Resolved, (await AlertOfAsync(taskId)).Status);
        // One hint, of the round that committed: the rolled-back resolution was never delivered.
        await probe.WaitForAsync(all => all.Any(n => n.Events.Any(e => e.AlertId == alert.AlertId)), "alert.changed of the resolved alert");
        Assert.Equal(
            [RealtimeHint.AlertChanged(alert.AlertId, AlertType.ProcessingFailed, AlertStatus.Resolved, productId: null, channelId: null)],
            probe.Events.Where(e => e.AlertId == alert.AlertId));
    }

    /// <summary>
    /// An export the worker failed for good (more rows than its limit), with its open <c>ProcessingFailed</c> alert, and
    /// a file under its key - as if it had failed after the upload.
    /// </summary>
    private async Task<Guid> FailedExportWithFileAsync(HttpClient manager, string instanceId)
    {
        var token = TestData.UniqueSuffix().ToUpperInvariant();
        await SeedProductsAsync(factory, ($"XA-{token}-A", "Alert A", 1m, 0, 1, null), ($"XA-{token}-B", "Alert B", 1m, 0, 1, null));
        var taskId = await RequestInventoryAsync(manager, $"XA-{token}");

        using var workers = await StartWorkersAsync(factory, broker, instanceId, new Dictionary<string, string?> { ["Reports:MaxRows"] = "1" });
        try
        {
            await WaitForStatusAsync(factory, taskId, ProcessingTaskStatus.Failed);
        }
        finally
        {
            await workers.StopAsync();
        }

        await _storage.PutAsync(ReportExport.ObjectKeyOf(taskId), [1, 2, 3], ReportFiles.ContentType);
        return taskId;
    }
}
