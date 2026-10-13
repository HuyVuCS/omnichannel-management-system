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
            var result = await scope.ServiceProvider.GetRequiredService<IProductImageCleanup>()
                .RunAsync(runId, WorkerName, dryRun: false, stoppingToken);

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

/// <summary>
/// The image clean-up as a look without a touch, for an operator: <c>dotnet Omnichannel.Workers.dll
/// --image-cleanup-dry-run=true</c> (in the production stack: <c>docker compose run --rm --no-deps workers
/// --image-cleanup-dry-run=true</c>) builds the services of the host <b>without starting any worker</b>, runs one
/// clean-up that deletes nothing and writes nothing, logs what a real run would delete now, and ends.
/// </summary>
public static class ImageCleanupDryRun
{
    /// <summary>Configuration key of the switch (a command-line argument).</summary>
    public const string Switch = "image-cleanup-dry-run";

    /// <summary>Most keys of each kind the report lists.</summary>
    public const int MaxListed = 100;

    public static async Task<ImageCleanupResult> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ImageCleanupDryRun).FullName!);
        var settings = scope.ServiceProvider.GetRequiredService<IOptions<ImageCleanupOptions>>().Value;
        var result = await scope.ServiceProvider.GetRequiredService<IProductImageCleanup>()
            .RunAsync(Guid.CreateVersion7(), ImageCleanupService.WorkerName, dryRun: true, cancellationToken);

        if (!result.Ran)
        {
            logger.LogWarning("Image clean-up dry run: a clean-up of a running instance holds the lock; nothing was looked at. Try again.");
            return result;
        }

        logger.LogInformation(
            "Image clean-up dry run (nothing deleted, nothing written): {Listed} objects listed; a run now would delete {Uploads} uploads and {Images} unreferenced images older than {MinAge}; {Referenced} old images in use would be kept, {Unknown} unknown keys left alone; stopped at the limit of {MaxPerRun}: {MoreExpected}",
            result.Listed, result.DeletedUploads.Count, result.DeletedImages.Count, settings.MinAge, result.Referenced, result.Unknown,
            settings.MaxPerRun, result.MoreExpected);
        foreach (var key in result.DeletedUploads.Take(MaxListed))
        {
            logger.LogInformation("Image clean-up dry run: would delete the upload {ObjectKey}", key);
        }

        foreach (var key in result.DeletedImages.Take(MaxListed))
        {
            logger.LogInformation("Image clean-up dry run: would delete the unreferenced image {ObjectKey}", key);
        }

        return result;
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
