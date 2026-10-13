using System.Diagnostics;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Products;

namespace Omnichannel.Workers.ImageCleanupWorker;

/// <summary>
/// Runs the image clean-up (<see cref="IProductImageCleanup"/>) every <c>ImageCleanup:Interval</c>, the first time
/// shortly after start-up. A run that fails (database or storage down) is logged and the next round starts over; the
/// clean-up never stops the host. Several instances may run this service: a round that finds another instance's run in
/// progress is skipped.
/// </summary>
public sealed class ImageCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<ImageCleanupOptions> options,
    IOptions<WorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<ImageCleanupService> logger) : BackgroundService
{
    public const string WorkerName = "ImageCleanup";
    public const string Actor = "system:image-cleanup";

    /// <summary>The longest wait before the first round: what became old while the host was down does not wait a whole interval.</summary>
    public static readonly TimeSpan MaxInitialDelay = TimeSpan.FromSeconds(30);

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
            logger.LogInformation("The image clean-up is switched off (ImageCleanup:Enabled = false)");
            return;
        }

        logger.LogInformation(
            "Image clean-up started: every {Interval}, uploads and unreferenced image objects older than {MinAge} are deleted, at most {MaxPerRun} per run",
            settings.Interval, settings.MinAge, settings.MaxPerRun);
        try
        {
            await Task.Delay(settings.Interval < MaxInitialDelay ? settings.Interval : MaxInitialDelay, timeProvider, stoppingToken);
            while (true)
            {
                await RunRoundAsync(stoppingToken);
                await Task.Delay(settings.Interval, timeProvider, stoppingToken);
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
        var correlationId = ImageCleanupIds.CorrelationIdOf(runId);
        using var runScope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        var watch = Stopwatch.StartNew();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<WorkerCurrentUser>().UserId = Actor;
            scope.ServiceProvider.GetRequiredService<WorkerCorrelationContext>().CorrelationId = correlationId;
            var result = await scope.ServiceProvider.GetRequiredService<IProductImageCleanup>().RunAsync(runId, WorkerName, stoppingToken);

            if (!result.Ran)
            {
                logger.LogDebug("Image clean-up round skipped: another instance is running one");
                return;
            }

            // Every run is one line (four a day by default): what it looked at is the evidence that it deletes nothing in use.
            logger.LogInformation(
                "Image clean-up run {RunId}: {Listed} objects listed, {DeletedUploads} uploads and {DeletedImages} unreferenced images deleted, {Referenced} old images in use kept, {Unknown} unknown keys left alone, {Errors} not deleted, more waiting: {MoreExpected}; {DurationMs} ms",
                result.RunId, result.Listed, result.DeletedUploads.Count, result.DeletedImages.Count, result.Referenced, result.Unknown,
                result.Errors, result.MoreExpected, watch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Image clean-up run {RunId} failed; the next round starts over", runId);
        }
    }
}

/// <summary><see cref="ImageCleanupOptionsRules"/> at start-up: a host with settings out of range does not start.</summary>
public sealed class ImageCleanupOptionsValidator : IValidateOptions<ImageCleanupOptions>
{
    public ValidateOptionsResult Validate(string? name, ImageCleanupOptions options)
    {
        var failures = ImageCleanupOptionsRules.Validate(options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
