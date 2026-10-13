using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Entities;
using Omnichannel.Workers.ImageCleanupWorker;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 026c Part B2: the image clean-up as the Workers host runs it. The bucket is this class's own (a storage
/// emulator started for each test), so a run sees exactly the objects of the test; the host's clock is a day ahead,
/// which makes every object of the test old. The rules of a run are tested in <c>ProductImageHygieneTests</c>.
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ImageCleanupServiceTests(WorkersFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly StorageEmulator _storage = new();

    public Task InitializeAsync() => _storage.StartAsync();

    public async Task DisposeAsync() => await _storage.DisposeAsync();

    private Dictionary<string, string?> Settings(bool enabled) => new()
    {
        ["ObjectStorage:ServiceUrl"] = _storage.ServiceUrl,
        ["ObjectStorage:Region"] = StorageEmulator.Region,
        ["ObjectStorage:ForcePathStyle"] = "true",
        ["ObjectStorage:BucketName"] = StorageEmulator.Bucket,
        ["ObjectStorage:AccessKeyId"] = _storage.AccessKeyId,
        ["ObjectStorage:SecretAccessKey"] = _storage.SecretAccessKey,
        ["ImageCleanup:Enabled"] = enabled ? "true" : "false",
        ["ImageCleanup:Interval"] = "00:00:01"
    };

    /// <summary>Log capture and a clock a day and an hour ahead: everything in the bucket is older than the default MinAge.</summary>
    private static Action<IServiceCollection> With(CapturingLoggerProvider logs) => services =>
    {
        services.AddSingleton<ILoggerProvider>(logs);
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new AheadClock(TimeSpan.FromHours(25))));
    };

    [Fact]
    public async Task The_service_deletes_what_is_old_and_unreferenced_keeps_the_image_in_use_and_does_nothing_when_switched_off()
    {
        var productId = await fixture.WithDbAsync(async db =>
        {
            var product = new Product($"CLN-{Guid.NewGuid():N}"[..20].ToUpperInvariant(), "Clean-up", 1m, 0, null);
            product.SetImage(ProductImageKey.NewFinalFor(product.ProductId, Guid.CreateVersion7()), ProductImageRules.Png, 3, DateTimeOffset.UtcNow);
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return product.ProductId;
        });
        var imageKey = await fixture.WithDbAsync(db => db.Products.Where(p => p.ProductId == productId).Select(p => p.ImageKey!).SingleAsync());
        var upload = ProductImageKey.NewStagingFor(productId);
        var orphan = ProductImageKey.NewFinalFor(productId, Guid.CreateVersion7());
        var export = $"exports/{Guid.NewGuid()}.xlsx";
        foreach (var key in new[] { imageKey, upload, orphan, export })
        {
            await _storage.PutAsync(key, [1, 2, 3], ProductImageRules.Png);
        }

        // Switched off: the host says so once and touches nothing.
        var offLogs = new CapturingLoggerProvider();
        using (var off = fixture.CreateHost("images-off", Settings(enabled: false), With(offLogs)))
        {
            await off.StartAsync();
            await Eventually.WaitAsync(
                () => Task.FromResult(offLogs.Count(typeof(ImageCleanupService).FullName!, LogLevel.Information, "switched off")),
                count => count == 1, Timeout, "the service says that it is switched off");
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            await off.StopAsync();
        }

        Assert.Equal(new[] { imageKey, orphan }.Order(StringComparer.Ordinal), await _storage.KeysAsync("products/"));
        Assert.Equal([upload], await _storage.KeysAsync("uploads/"));

        // Switched on: the first round comes a second after the start (the interval, when it is shorter than 30 s).
        var logs = new CapturingLoggerProvider();
        using var on = fixture.CreateHost("images-on", Settings(enabled: true), With(logs));
        await on.StartAsync();
        try
        {
            await Eventually.WaitAsync(
                async () => (await _storage.KeysAsync("products/")).Count + (await _storage.KeysAsync("uploads/")).Count,
                count => count == 1, Timeout, "the upload and the unreferenced image are deleted");
            // Further rounds follow, and find nothing more to delete.
            await Eventually.WaitAsync(
                () => Task.FromResult(logs.Count(typeof(ImageCleanupService).FullName!, LogLevel.Information, "Image clean-up run")),
                count => count >= 3, Timeout, "the service runs every interval");
        }
        finally
        {
            await on.StopAsync();
        }

        // The image a product refers to and the export file are what is left.
        Assert.Equal([imageKey], await _storage.KeysAsync("products/"));
        Assert.Empty(await _storage.KeysAsync("uploads/"));
        Assert.True(await _storage.ExistsAsync(export));

        // One event, of the run that deleted something, under the service's name; every run is one log line.
        var run = Assert.Single(
            await fixture.WithDbAsync(db => db.SystemEventLogs.AsNoTracking().Where(e => e.EventType == ProductImageEvents.OrphansDeleted).ToListAsync()),
            e => e.Payload!.Contains(productId.ToString(), StringComparison.Ordinal));
        Assert.Equal(ImageCleanupService.WorkerName, run.WorkerName);
        Assert.StartsWith(ImageCleanupIds.CorrelationIdPrefix, run.CorrelationId, StringComparison.Ordinal);
        Assert.Contains(upload, run.Payload!, StringComparison.Ordinal);
        Assert.Contains(orphan, run.Payload!, StringComparison.Ordinal);
        Assert.DoesNotContain(imageKey, run.Payload!, StringComparison.Ordinal);
        Assert.Equal(1, logs.Count(typeof(ImageCleanupService).FullName!, LogLevel.Information, "Image clean-up started"));
        Assert.Equal(0, logs.Count(typeof(ImageCleanupService).FullName!, LogLevel.Error, "failed"));
    }

    [Fact]
    public async Task A_round_that_cannot_reach_the_storage_is_logged_and_the_next_one_starts_over()
    {
        var upload = ProductImageKey.NewStagingFor(Guid.CreateVersion7());
        await _storage.PutAsync(upload, [1, 2, 3], ProductImageRules.Png);
        await _storage.StopAsync();
        var logs = new CapturingLoggerProvider();
        var settings = Settings(enabled: true);
        settings["ObjectStorage:RequestTimeout"] = "00:00:01";

        using var host = fixture.CreateHost("images-down", settings, With(logs));
        await host.StartAsync();
        try
        {
            await Eventually.WaitAsync(
                () => Task.FromResult(logs.Count(typeof(ImageCleanupService).FullName!, LogLevel.Error, "failed; the next round starts over")),
                count => count >= 1, Timeout, "a round fails while the storage is down");
            await _storage.StartAsync();

            await Eventually.WaitAsync(() => _storage.ExistsAsync(upload), exists => !exists, Timeout, "a later round deletes the upload");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    // Shorter than four confirmation deadlines (two minutes): a copy that young may still become an image.
    [InlineData("ImageCleanup:MinAge", "00:01:59", "ImageCleanup:MinAge")]
    [InlineData("ImageCleanup:Interval", "00:00:00", "ImageCleanup:Interval")]
    [InlineData("ImageCleanup:MaxPerRun", "0", "ImageCleanup:MaxPerRun")]
    public async Task A_host_with_invalid_settings_of_the_clean_up_does_not_start(string key, string value, string named)
    {
        var settings = Settings(enabled: false);
        settings[key] = value;
        using var host = fixture.CreateHost("images-invalid", settings);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.StartsWith(named, Assert.Single(exception.Failures), StringComparison.Ordinal);
    }

    /// <summary>The real time plus a fixed distance; timers run as they do (only "now" is moved).</summary>
    private sealed class AheadClock(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + ahead;
    }
}
