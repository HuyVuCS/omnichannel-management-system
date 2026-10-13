using System.Diagnostics;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Processing;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.OutboxPublisher;

namespace Omnichannel.Workers.StaleTaskWorker;

/// <summary>
/// Runs the stale-task sweep (<see cref="IStaleTaskSweep"/>) every <c>StaleTasks:Interval</c>. The first run waits a
/// whole interval after start-up: after a time without Workers the queues hold the messages of tasks that look stale,
/// and the consumers get that long to start them (a started task is no longer stale) before anything is failed.
/// A run that fails (database down) is logged and the next round starts over; the sweep never stops the host. Several
/// instances may run this service: a round that finds another instance's run in progress is skipped.
/// </summary>
public sealed class StaleTaskSweepService(
    IServiceScopeFactory scopeFactory,
    IOptions<StaleTaskOptions> options,
    IOptions<WorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<StaleTaskSweepService> logger) : BackgroundService
{
    public const string WorkerName = "StaleTaskSweep";
    public const string Actor = "system:stale-task-sweep";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["WorkerName"] = WorkerName,
            ["WorkerInstanceId"] = workerOptions.Value.InstanceId
        });

        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("The stale-task sweep is switched off (StaleTasks:Enabled = false)");
            return;
        }

        logger.LogInformation(
            "Stale-task sweep started: every {Interval}, a task without progress for {StaleAfter} is failed, at most {MaxPerRun} per run",
            settings.Interval, settings.StaleAfter, settings.MaxPerRun);
        try
        {
            while (true)
            {
                await Task.Delay(settings.Interval, timeProvider, stoppingToken);
                await RunRoundAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping; what an unfinished run did not reach is found by the next one.
        }
    }

    private async Task RunRoundAsync(CancellationToken stoppingToken)
    {
        var runId = Guid.CreateVersion7();
        var correlationId = StaleTaskIds.CorrelationIdOf(runId);
        using var runScope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        var watch = Stopwatch.StartNew();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<WorkerCurrentUser>().UserId = Actor;
            scope.ServiceProvider.GetRequiredService<WorkerCorrelationContext>().CorrelationId = correlationId;
            var result = await scope.ServiceProvider.GetRequiredService<IStaleTaskSweep>()
                .RunAsync(runId, WorkerName, workerOptions.Value.InstanceId, stoppingToken);

            if (!result.Ran)
            {
                logger.LogDebug("Stale-task round skipped: another instance is running one");
                return;
            }

            if (result.Stale == 0)
            {
                logger.LogDebug("Stale-task run {RunId}: nothing stale; {DurationMs} ms", result.RunId, watch.ElapsedMilliseconds);
                return;
            }

            logger.LogWarning(
                "Stale-task run {RunId}: {Stale} stale, {Failed} failed, {Skipped} touched meanwhile and left alone, {Errors} not written, more waiting: {MoreExpected}; {DurationMs} ms",
                result.RunId, result.Stale, result.Failed.Count, result.Skipped, result.Errors, result.MoreExpected, watch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Stale-task run {RunId} failed; the next round starts over", runId);
        }
    }
}

/// <summary>
/// <see cref="StaleTaskOptionsRules"/> against what this host waits for: the retry tiers of the consumers and the
/// backoff of the Outbox Publisher. A host whose <c>StaleTasks:StaleAfter</c> is too short for them does not start.
/// </summary>
public sealed class StaleTaskOptionsValidator(
    IOptions<RabbitMqOptions> rabbitMq,
    IOptions<OutboxPublisherOptions> outboxPublisher) : IValidateOptions<StaleTaskOptions>
{
    public ValidateOptionsResult Validate(string? name, StaleTaskOptions options)
    {
        var failures = StaleTaskOptionsRules.Validate(
            options, rabbitMq.Value.EffectiveRetryDelays.Max(), LongestWaitOf(outboxPublisher.Value));
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// The longest time the Outbox Publisher keeps one message Pending: the sum of its backoffs before the attempt that
    /// makes the row <c>Failed</c> (each at its upper bound, <see cref="RetryBackoff.Compute"/> with full jitter).
    /// </summary>
    public static TimeSpan LongestWaitOf(OutboxPublisherOptions publisher)
    {
        var total = TimeSpan.Zero;
        for (var failedAttempts = 1; failedAttempts < publisher.MaxAttempts; failedAttempts++)
        {
            var backoff = publisher.InitialBackoff * Math.Pow(2, Math.Min(failedAttempts - 1, 30));
            total += backoff < publisher.MaxBackoff ? backoff : publisher.MaxBackoff;
        }

        return total;
    }
}
