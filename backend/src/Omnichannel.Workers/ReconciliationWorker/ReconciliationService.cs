using System.Diagnostics;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Channels;
using Omnichannel.Infrastructure.Persistence.Channels;

namespace Omnichannel.Workers.ReconciliationWorker;

/// <summary>
/// Runs a stock reconciliation (<see cref="StockReconciliation"/>) every <c>Reconciliation:Interval</c>, the first time
/// shortly after start-up. Periodic only: there is no way to ask for a run. A run that fails (database down) is logged
/// and the next round starts over; reconciliation never stops the host. Several instances may run this service: a round
/// that finds another instance's run in progress is skipped.
/// </summary>
public sealed class ReconciliationService(
    IServiceScopeFactory scopeFactory,
    IOptions<ReconciliationOptions> options,
    IOptions<WorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<ReconciliationService> logger) : BackgroundService
{
    public const string WorkerName = "ReconciliationWorker";
    public const string Actor = "system:reconciliation-worker";

    /// <summary>The longest wait before the first round: drift that built up while the host was down does not wait a whole interval.</summary>
    public static readonly TimeSpan MaxInitialDelay = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["WorkerName"] = WorkerName,
            ["WorkerInstanceId"] = workerOptions.Value.InstanceId
        });

        if (!options.Value.Enabled)
        {
            logger.LogInformation("Reconciliation is switched off (Reconciliation:Enabled = false)");
            return;
        }

        var interval = options.Value.Interval;
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
            // The host is stopping; what an unfinished run did not look at is found by the next one.
        }
    }

    private async Task RunRoundAsync(CancellationToken stoppingToken)
    {
        var runId = Guid.CreateVersion7();
        var correlationId = ReconciliationIds.CorrelationIdOf(runId);
        using var runScope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        var watch = Stopwatch.StartNew();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<WorkerCurrentUser>().UserId = Actor;
            scope.ServiceProvider.GetRequiredService<WorkerCorrelationContext>().CorrelationId = correlationId;
            var result = await scope.ServiceProvider.GetRequiredService<StockReconciliation>().RunAsync(runId, WorkerName, stoppingToken);

            if (!result.Ran)
            {
                logger.LogDebug("Reconciliation round skipped: another instance is running one");
                return;
            }

            logger.LogInformation(
                "Reconciliation run {RunId}: {Checked} checked, {InSync} in sync, {Drifted} drifted, {TasksCreated} sync tasks created, {Deferred} deferred, {SkippedUnfinished} skipped (sync unfinished), {SkippedRejected} skipped (rejected by the channel), {Unreadable} channels not readable; {DurationMs} ms",
                result.RunId,
                result.Checked,
                result.InSync,
                result.Drifted,
                result.TasksCreated,
                result.Deferred,
                result.SkippedUnfinished,
                result.SkippedRejected,
                result.Unreadable.Count,
                watch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Reconciliation run {RunId} failed; the next round starts over", runId);
        }
    }
}
