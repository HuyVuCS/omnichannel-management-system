using Microsoft.Extensions.Options;
using Omnichannel.Application.Reports;
using Omnichannel.Infrastructure.Persistence.Reports;

namespace Omnichannel.Workers.ReportExportWorker;

/// <summary>
/// Runs the retention sweep of exports (<see cref="ReportExportCleanup"/>) every <c>Reports:CleanupInterval</c>, the first
/// time shortly after start-up. A round that fails (database or storage down) is logged and the next one tries again;
/// the sweep never stops the host. Several instances may run it at the same time.
/// </summary>
public sealed class ReportExportCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<ReportsOptions> options,
    IOptions<WorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<ReportExportCleanupService> logger) : BackgroundService
{
    public const string WorkerName = "ReportExportCleanup";
    public const string Actor = "system:report-export-cleanup";

    /// <summary>The longest wait before the first round: files that expired while the host was down do not wait a whole interval.</summary>
    public static readonly TimeSpan MaxInitialDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["WorkerName"] = WorkerName,
            ["WorkerInstanceId"] = workerOptions.Value.InstanceId
        });

        var interval = options.Value.CleanupInterval;
        try
        {
            await Task.Delay(interval < MaxInitialDelay ? interval : MaxInitialDelay, timeProvider, stoppingToken);
            while (true)
            {
                await RunRoundAsync(stoppingToken);
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping; an unfinished round is repeated after the next start.
        }
    }

    private async Task RunRoundAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<WorkerCurrentUser>().UserId = Actor;
            var result = await scope.ServiceProvider.GetRequiredService<ReportExportCleanup>().RunAsync(stoppingToken);

            if (result.Expired > 0 || result.Failed > 0)
            {
                logger.LogInformation(
                    "Export retention sweep: {Expired} exports expired, {Failed} files could not be deleted", result.Expired, result.Failed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Export retention sweep failed; it runs again after the interval");
        }
    }
}
