using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure;
using Omnichannel.Infrastructure.S3;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 026c Part B against real PostgreSQL and the storage emulator: what the provider does with a copy and a listing,
/// the confirmation when the upload changes under it, and the clean-up of image objects nothing refers to.
/// <para>
/// The code under test is the application's own, from a service provider of this class (the database and the bucket of
/// the API host) whose clock the test sets and whose storage calls the test can intercept - the two things a test
/// through HTTP cannot do. A clean-up run looks at the whole bucket: objects other tests of the collection left behind
/// may be deleted with it, so every assertion is about the objects of this test.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProductImageHygieneTests(ApiFactory factory) : IAsyncLifetime
{
    private const long AdvisoryLockKey = 0x4F4D4E495F494D47;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);
    private readonly HttpClient _browser = new();

    private StorageEmulator _storage = null!;

    public async Task InitializeAsync() => _storage = await factory.StorageAsync();

    public Task DisposeAsync()
    {
        _browser.Dispose();
        return Task.CompletedTask;
    }

    // ---- the provider: copy and listing (the emulator of development, SeaweedFS) -----------------------------------

    [Fact]
    public async Task A_copy_has_the_bytes_of_its_source_the_given_content_type_and_the_time_of_the_copy()
    {
        await using var tools = Tools(TimeProvider.System);
        var storage = tools.GetRequiredService<IObjectStorage>();
        var prefix = $"copy-test/{Guid.NewGuid():N}/";
        var content = Image(1500);
        await _storage.PutAsync($"{prefix}source", content, "image/png");
        var sourceWritten = (await ListAllAsync(storage, prefix)).Single().LastModified;
        // The storage keeps whole seconds: the copy must be told apart from its source.
        await Task.Delay(TimeSpan.FromSeconds(2.2));

        Assert.True(await storage.CopyAsync($"{prefix}source", $"{prefix}copy", "image/webp", CancellationToken.None));

        var listed = (await ListAllAsync(storage, prefix)).ToDictionary(o => o.Key[prefix.Length..]);
        Assert.Equal(["copy", "source"], listed.Keys.Order());
        // Written at the time of the copy, not of its source: the age the clean-up measures starts here.
        Assert.True(listed["copy"].LastModified >= sourceWritten.AddSeconds(2), $"{listed["copy"].LastModified:O} / {sourceWritten:O}");
        Assert.Equal(sourceWritten, listed["source"].LastModified);
        Assert.Equal((1500L, 1500L), (listed["copy"].Length, listed["source"].Length));
        // The content type is the one given, whatever the source was stored with; the bytes are the source's.
        Assert.Equal(new StoredObject(1500, "image/webp"), await storage.HeadAsync($"{prefix}copy", CancellationToken.None));
        Assert.Equal(new StoredObject(1500, "image/png"), await storage.HeadAsync($"{prefix}source", CancellationToken.None));
        using var download = await _browser.GetAsync(storage.PresignGet($"{prefix}copy", TimeSpan.FromMinutes(1)).Url);
        Assert.Equal("image/webp", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());

        // A copy replaces what is there; a source that does not exist is "false", and nothing is written.
        await _storage.PutAsync($"{prefix}other", Image(10), "image/jpeg");
        Assert.True(await storage.CopyAsync($"{prefix}other", $"{prefix}copy", "image/jpeg", CancellationToken.None));
        Assert.Equal(new StoredObject(10, "image/jpeg"), await storage.HeadAsync($"{prefix}copy", CancellationToken.None));
        Assert.False(await storage.CopyAsync($"{prefix}missing", $"{prefix}never", "image/png", CancellationToken.None));
        Assert.Null(await storage.HeadAsync($"{prefix}never", CancellationToken.None));
    }

    [Fact]
    public async Task A_listing_comes_in_pages_of_the_prefix_only_until_no_token_is_left()
    {
        await using var tools = Tools(TimeProvider.System);
        var storage = tools.GetRequiredService<IObjectStorage>();
        var id = Guid.NewGuid().ToString("N");
        foreach (var n in Enumerable.Range(0, 7))
        {
            await _storage.PutAsync($"list-test/{id}/{n:00}", Image(20 + n), "image/png");
        }

        // A key that only starts like the prefix's folder is not part of it.
        await _storage.PutAsync($"list-test/{id}-other/00", Image(20), "image/png");

        var pages = new List<StoredObjectPage>();
        string? token = null;
        do
        {
            var page = await storage.ListAsync($"list-test/{id}/", token, 3, CancellationToken.None);
            pages.Add(page);
            token = page.NextToken;
        }
        while (token is not null);

        Assert.Equal([3, 3, 1], pages.Select(p => p.Objects.Count));
        var objects = pages.SelectMany(p => p.Objects).ToList();
        Assert.Equal(Enumerable.Range(0, 7).Select(n => $"list-test/{id}/{n:00}"), objects.Select(o => o.Key).Order(StringComparer.Ordinal));
        Assert.Equal(Enumerable.Range(0, 7).Select(n => 20L + n), objects.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => o.Length));
        Assert.All(objects, o => Assert.InRange(o.LastModified, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(10)));

        var none = await storage.ListAsync($"list-test/{id}/none/", null, 3, CancellationToken.None);
        Assert.Equal((0, (string?)null), (none.Objects.Count, none.NextToken));
    }

    // ---- the confirmation when the upload changes under it -----------------------------------------------------------

    public enum Swap
    {
        /// <summary>Bytes that are no image, of the signed type and length.</summary>
        NoImage,

        /// <summary>A larger object than an image may be (a storage that does not enforce the signed length).</summary>
        TooLarge
    }

    [Theory]
    [InlineData(Swap.NoImage, "The content of the file is not an image of the type it was uploaded as.")]
    [InlineData(Swap.TooLarge, "The image must not be larger than 5 MB.")]
    public async Task An_upload_replaced_between_the_first_look_and_the_copy_is_rejected_by_the_check_of_the_copy(Swap swap, string message)
    {
        var product = await CreateProductAsync();
        var current = await SetImageAsync(product.ProductId, Image(700));
        var currentKey = await ImageKeyAsync(product.ProductId);
        var uploadKey = await UploadNewAsync(product.ProductId, Image(2048));
        var hooks = new StorageHooks
        {
            // The upload link is still valid: its holder writes the staging key again after the confirmation looked at
            // it and before the copy is made.
            BeforeCopy = (source, _) => _storage.PutAsync(
                source, swap == Swap.NoImage ? new byte[2048] : Image(5 * 1024 * 1024 + 1), ProductImageRules.Png)
        };
        await using var tools = Tools(TimeProvider.System, hooks);
        await using var scope = tools.CreateAsyncScope();

        var rejection = await Assert.ThrowsAsync<ValidationException>(() => scope.ServiceProvider.GetRequiredService<IProductImageService>()
            .ConfirmAsync(product.ProductId, new ConfirmProductImageRequest(uploadKey), CancellationToken.None));

        Assert.Equal(("objectKey", message), (Assert.Single(rejection.Errors).PropertyName, rejection.Errors.Single().ErrorMessage));
        // What was checked is what would have been referred to - the copy - and it is gone with the upload; the image
        // the product had is untouched.
        Assert.Equal(1, hooks.Copies);
        Assert.Equal([currentKey!], await ObjectsOfAsync(product.ProductId));
        Assert.Equal((currentKey, current.UpdatedAt), (await ImageKeyAsync(product.ProductId), (await GetAsync(product.ProductId)).Image?.UpdatedAt));
    }

    [Fact]
    public async Task A_confirmation_held_up_longer_than_the_deadline_after_its_copy_gives_up_and_removes_the_copy()
    {
        var product = await CreateProductAsync();
        var uploadKey = await UploadNewAsync(product.ProductId, Image(900));
        var clock = new TestClock();
        var hooks = new StorageHooks
        {
            AfterCopy = (_, _) =>
            {
                // A storage that barely answers, or a long wait for the product row.
                clock.Offset += ProductImageRules.ConfirmationDeadline + TimeSpan.FromSeconds(1);
                return Task.CompletedTask;
            }
        };
        await using var tools = Tools(clock, hooks);
        await using (var scope = tools.CreateAsyncScope())
        {
            var failure = await Assert.ThrowsAsync<ObjectStorageException>(() => scope.ServiceProvider.GetRequiredService<IProductImageService>()
                .ConfirmAsync(product.ProductId, new ConfirmProductImageRequest(uploadKey), CancellationToken.None));
            Assert.Equal("confirm", failure.Operation);
        }

        // Nothing refers to a copy the clean-up could take for an orphan: there is none. The upload is still there.
        Assert.Null(await ImageKeyAsync(product.ProductId));
        Assert.Equal([uploadKey], await ObjectsOfAsync(product.ProductId));
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(product.ProductId, uploadKey)).StatusCode);
    }

    [Fact]
    public async Task One_second_before_the_deadline_the_confirmation_still_refers_to_its_copy()
    {
        var product = await CreateProductAsync();
        var uploadKey = await UploadNewAsync(product.ProductId, Image(900));
        var clock = new TestClock();
        var hooks = new StorageHooks
        {
            AfterCopy = (_, _) =>
            {
                clock.Offset += ProductImageRules.ConfirmationDeadline - TimeSpan.FromSeconds(1);
                return Task.CompletedTask;
            }
        };
        await using var tools = Tools(clock, hooks);
        await using var scope = tools.CreateAsyncScope();

        var confirmed = await scope.ServiceProvider.GetRequiredService<IProductImageService>()
            .ConfirmAsync(product.ProductId, new ConfirmProductImageRequest(uploadKey), CancellationToken.None);

        Assert.NotNull(confirmed.Image);
        Assert.Equal([(await ImageKeyAsync(product.ProductId))!], await ObjectsOfAsync(product.ProductId));
    }

    [Fact]
    public async Task A_confirmation_whose_transaction_fails_leaves_the_product_as_it_was_and_can_be_repeated()
    {
        var product = await CreateProductAsync();
        var uploadKey = await UploadNewAsync(product.ProductId, Image(900));

        factory.CommitFailures.FailNextCommit();
        var failed = await ConfirmAsync(product.ProductId, uploadKey);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Null(await ImageKeyAsync(product.ProductId));
        // The upload is still there, and so is the copy: a commit that failed says nothing about its outcome, so the
        // copy is left to the clean-up, which deletes it only if no product refers to it.
        var left = await ObjectsOfAsync(product.ProductId);
        Assert.Equal(2, left.Count);
        Assert.Contains(uploadKey, left);
        var orphan = left.Single(key => key != uploadKey);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(product.ProductId, uploadKey)).StatusCode);

        // A new copy is the image; the first copy is an orphan nothing refers to.
        var imageKey = await ImageKeyAsync(product.ProductId);
        Assert.NotEqual(orphan, imageKey);
        Assert.Equal(new[] { imageKey!, orphan }.Order(StringComparer.Ordinal), await ObjectsOfAsync(product.ProductId));

        // ... until it is old: the clean-up takes it and leaves the image.
        var result = await RunCleanupAsync(new FixedClock(DateTimeOffset.UtcNow + MinAge + TimeSpan.FromHours(1)));
        Assert.Contains(orphan, result.DeletedImages);
        Assert.Equal([imageKey!], await ObjectsOfAsync(product.ProductId));
        using var download = await _browser.GetAsync((await GetAsync(product.ProductId)).Image!.Url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }

    // ---- the clean-up ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_clean_up_deletes_old_uploads_and_old_unreferenced_images_and_nothing_else()
    {
        var product = await CreateProductAsync();
        var other = await CreateProductAsync();
        var exportKey = $"exports/{Guid.NewGuid()}.xlsx";
        var unknownKey = $"products/{product.ProductId}/readme-{Guid.NewGuid():N}.txt";

        // Old: an upload that was never confirmed, an image a product refers to, an image nothing refers to (a
        // replaced image whose delete failed), an export file, and something under the prefix the application never wrote.
        var oldUpload = await UploadNewAsync(product.ProductId, Image(300));
        await SetImageAsync(product.ProductId, Image(400));
        var imageKey = (await ImageKeyAsync(product.ProductId))!;
        var oldOrphan = await PutOrphanAsync(other.ProductId);
        await _storage.PutAsync(exportKey, Image(50), "application/octet-stream");
        await _storage.PutAsync(unknownKey, Image(50), "text/plain");
        // Young: the same two kinds, written a few seconds later.
        await Task.Delay(TimeSpan.FromSeconds(3.2));
        var youngUpload = await UploadNewAsync(other.ProductId, Image(300));
        var youngOrphan = await PutOrphanAsync(other.ProductId);

        // "Now" for the clean-up: MinAge after a moment between the two groups, by the storage's own clock.
        var written = await WrittenAsync(product.ProductId, other.ProductId);
        var oldest = new[] { oldUpload, imageKey, oldOrphan }.Max(key => written[key]);
        var youngest = new[] { youngUpload, youngOrphan }.Min(key => written[key]);
        Assert.True(youngest - oldest >= TimeSpan.FromSeconds(2), $"{oldest:O} / {youngest:O}");
        var clock = new FixedClock(oldest + TimeSpan.FromSeconds(1) + MinAge);
        var since = DateTimeOffset.UtcNow;

        var result = await RunCleanupAsync(clock);

        Assert.True(result.Ran);
        Assert.Contains(oldUpload, result.DeletedUploads);
        Assert.Contains(oldOrphan, result.DeletedImages);
        Assert.DoesNotContain(youngUpload, result.DeletedUploads);
        Assert.DoesNotContain(youngOrphan, result.DeletedImages);
        Assert.DoesNotContain(imageKey, result.DeletedImages);
        Assert.True(result.Referenced >= 1);
        Assert.True(result.Unknown >= 1);
        Assert.Equal(0, result.Errors);
        // In the bucket: the image in use, the young objects, the unknown key and the export file are still there.
        Assert.Equal(
            new[] { imageKey, unknownKey }.Order(StringComparer.Ordinal),
            await ObjectsOfAsync(product.ProductId));
        Assert.Equal(new[] { youngOrphan, youngUpload }.Order(StringComparer.Ordinal), await ObjectsOfAsync(other.ProductId));
        Assert.True(await _storage.ExistsAsync(exportKey));
        using var download = await _browser.GetAsync((await GetAsync(product.ProductId)).Image!.Url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        // One event for the run: counts, and the keys (capped).
        var run = await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .SingleAsync(e => e.EventType == ProductImageEvents.OrphansDeleted && e.CorrelationId == ImageCleanupIds.CorrelationIdOf(result.RunId)));
        Assert.Equal((SystemEventLevel.Information, "ImageCleanupTest", clock.GetUtcNow()), (run.Level, run.WorkerName, run.CreatedAt));
        using var payload = JsonDocument.Parse(run.Payload!);
        Assert.Equal(result.RunId, payload.RootElement.GetProperty("runId").GetGuid());
        Assert.Equal((long)MinAge.TotalSeconds, payload.RootElement.GetProperty("minAgeSeconds").GetInt64());
        Assert.Equal(result.DeletedUploads.Count, payload.RootElement.GetProperty("uploads").GetInt32());
        Assert.Equal(result.DeletedImages.Count, payload.RootElement.GetProperty("images").GetInt32());
        var keys = payload.RootElement.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).ToList();
        Assert.Equal(Math.Min(ProductImageEvents.OrphansDeletedMaxListed, result.DeletedUploads.Count + result.DeletedImages.Count), keys.Count);
        Assert.Equal(
            Math.Max(0, result.DeletedUploads.Count + result.DeletedImages.Count - ProductImageEvents.OrphansDeletedMaxListed),
            payload.RootElement.GetProperty("notListed").GetInt32());
        // ... and readable through the API with exactly these fields.
        var listed = await factory.CreateClientAs(AppRoles.Admin).GetFromJsonAsync<JsonElement>(
            $"/api/system-events?eventType={ProductImageEvents.OrphansDeleted}&from={Uri.EscapeDataString(since.AddMinutes(-1).ToString("O"))}&pageSize=100",
            TestData.Json);
        Assert.Contains(listed.GetProperty("items").EnumerateArray(), e => e.GetProperty("correlationId").GetString() == run.CorrelationId);

        // A second run at the same moment finds nothing old that is unreferenced, and writes no event.
        var again = await RunCleanupAsync(clock);
        Assert.Equal((0, 0), (again.DeletedUploads.Count, again.DeletedImages.Count));
        Assert.False(await factory.QueryDbAsync(db => db.SystemEventLogs.AnyAsync(e => e.CorrelationId == ImageCleanupIds.CorrelationIdOf(again.RunId))));
    }

    [Fact]
    public async Task An_object_a_product_starts_to_refer_to_between_the_listing_and_the_question_is_kept()
    {
        var product = await CreateProductAsync();
        var orphan = await PutOrphanAsync(product.ProductId);
        var bystander = await PutOrphanAsync(product.ProductId);
        var referred = false;
        var hooks = new StorageHooks
        {
            // The page that lists the object has come back; before the clean-up asks the database, a product refers to it.
            AfterList = async (_, page) =>
            {
                if (!referred && page.Objects.Any(o => o.Key == orphan))
                {
                    referred = true;
                    await factory.QueryDbAsync(async db =>
                    {
                        var row = await db.Products.SingleAsync(p => p.ProductId == product.ProductId);
                        row.SetImage(orphan, ProductImageRules.Png, 64, DateTimeOffset.UtcNow);
                        return await db.SaveChangesAsync();
                    });
                }
            }
        };

        var result = await RunCleanupAsync(new FixedClock(DateTimeOffset.UtcNow + MinAge + TimeSpan.FromHours(1)), hooks);

        Assert.True(referred);
        Assert.DoesNotContain(orphan, result.DeletedImages);
        Assert.Contains(bystander, result.DeletedImages);
        Assert.Equal([orphan], await ObjectsOfAsync(product.ProductId));
        Assert.Equal(orphan, await ImageKeyAsync(product.ProductId));
    }

    [Fact]
    public async Task A_run_stops_at_its_limit_and_the_next_one_goes_on_and_an_object_that_cannot_be_deleted_is_tried_again()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow + MinAge + TimeSpan.FromHours(1));
        // Whatever earlier tests left behind goes first, so the numbers below are this test's.
        await RunCleanupAsync(clock);
        var product = await CreateProductAsync();
        var uploads = new List<string>();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            uploads.Add(await UploadNewAsync(product.ProductId, Image(100)));
        }

        var orphan = await PutOrphanAsync(product.ProductId);
        var refused = uploads[0];
        var hooks = new StorageHooks { FailDelete = key => key == refused };

        // At most two per run; one of the uploads cannot be deleted this time.
        var first = await RunCleanupAsync(clock, hooks, maxPerRun: 2);

        Assert.Equal((2, 0, 1, true), (first.DeletedUploads.Count, first.DeletedImages.Count, first.Errors, first.MoreExpected));
        Assert.Equal(uploads.Where(u => u != refused).Order(StringComparer.Ordinal), first.DeletedUploads.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { orphan, refused }.Order(StringComparer.Ordinal), await ObjectsOfAsync(product.ProductId));

        // The next run takes what is left: the upload that failed, and the image the first run did not reach.
        var second = await RunCleanupAsync(clock, maxPerRun: 2);

        Assert.Equal([refused], second.DeletedUploads);
        Assert.Equal([orphan], second.DeletedImages);
        Assert.Equal((0, true), (second.Errors, second.MoreExpected));
        Assert.Empty(await ObjectsOfAsync(product.ProductId));
        var third = await RunCleanupAsync(clock, maxPerRun: 2);
        Assert.Equal((0, 0, false), (third.DeletedUploads.Count, third.DeletedImages.Count, third.MoreExpected));
    }

    [Fact]
    public async Task A_dry_run_reports_what_a_run_would_delete_and_deletes_and_writes_nothing()
    {
        var product = await CreateProductAsync();
        var upload = await UploadNewAsync(product.ProductId, Image(100));
        await SetImageAsync(product.ProductId, Image(200));
        var imageKey = (await ImageKeyAsync(product.ProductId))!;
        var orphan = await PutOrphanAsync(product.ProductId);
        var clock = new FixedClock(DateTimeOffset.UtcNow + MinAge + TimeSpan.FromHours(1));
        var deletes = 0;
        var hooks = new StorageHooks { FailDelete = _ => { deletes++; return false; } };

        var dry = await RunCleanupAsync(clock, hooks, dryRun: true);

        Assert.True(dry.Ran);
        Assert.Contains(upload, dry.DeletedUploads);
        Assert.Contains(orphan, dry.DeletedImages);
        Assert.DoesNotContain(imageKey, dry.DeletedImages);
        // Nothing was deleted - no delete was even sent - and no event was written.
        Assert.Equal(0, deletes);
        Assert.Equal(new[] { imageKey, orphan, upload }.Order(StringComparer.Ordinal), await ObjectsOfAsync(product.ProductId));
        Assert.False(await factory.QueryDbAsync(db => db.SystemEventLogs.AnyAsync(e => e.CorrelationId == ImageCleanupIds.CorrelationIdOf(dry.RunId))));

        // The run that follows deletes exactly what the dry run named (of this product).
        var real = await RunCleanupAsync(clock);
        Assert.Contains(upload, real.DeletedUploads);
        Assert.Contains(orphan, real.DeletedImages);
        Assert.Equal([imageKey], await ObjectsOfAsync(product.ProductId));
    }

    [Fact]
    public async Task While_another_instance_runs_a_clean_up_a_round_looks_at_nothing()
    {
        var product = await CreateProductAsync();
        var upload = await UploadNewAsync(product.ProductId, Image(100));
        var clock = new FixedClock(DateTimeOffset.UtcNow + MinAge + TimeSpan.FromHours(1));

        ImageCleanupResult skipped;
        await using (var other = new NpgsqlConnection(factory.DatabaseConnectionString))
        {
            await other.OpenAsync();
            await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock($1)", other) { Parameters = { new() { Value = AdvisoryLockKey } } })
            {
                await take.ExecuteNonQueryAsync();
            }

            skipped = await RunCleanupAsync(clock);

            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", other) { Parameters = { new() { Value = AdvisoryLockKey } } };
            await release.ExecuteNonQueryAsync();
        }

        Assert.Equal((false, 0, 0), (skipped.Ran, skipped.Listed, skipped.DeletedUploads.Count));
        Assert.True(await _storage.ExistsAsync(upload));

        // The lock is free again (also after a run of its own): the next round does the work.
        var ran = await RunCleanupAsync(clock);
        Assert.True(ran.Ran);
        Assert.Contains(upload, ran.DeletedUploads);
        Assert.True((await RunCleanupAsync(clock)).Ran);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The persistence services of the application on the API host's database and bucket, with the clock and the
    /// storage of the test.
    /// </summary>
    private ServiceProvider Tools(TimeProvider clock, StorageHooks? hooks = null, int? maxPerRun = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = factory.DatabaseConnectionString,
            ["ObjectStorage:ServiceUrl"] = _storage.ServiceUrl,
            ["ObjectStorage:Region"] = StorageEmulator.Region,
            ["ObjectStorage:ForcePathStyle"] = "true",
            ["ObjectStorage:BucketName"] = StorageEmulator.Bucket,
            ["ObjectStorage:AccessKeyId"] = _storage.AccessKeyId,
            ["ObjectStorage:SecretAccessKey"] = _storage.SecretAccessKey
        };
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .AddLogging()
            .AddSingleton(clock)
            .AddScoped<ICurrentUser, TestActor>()
            .AddScoped<ICorrelationContext, TestActor>()
            .AddInfrastructure()
            .AddObjectStorage()
            .Configure<ImageCleanupOptions>(options =>
            {
                options.MinAge = MinAge;
                options.MaxPerRun = maxPerRun ?? ImageCleanupOptionsRules.MaxPerRunLimit;
            });
        if (hooks is not null)
        {
            var storage = services.Single(d => d.ServiceType == typeof(IObjectStorage));
            services.Remove(storage);
            services.AddSingleton<IObjectStorage>(sp => new HookedStorage(
                (IObjectStorage)ActivatorUtilities.CreateInstance(sp, storage.ImplementationType!), hooks));
        }

        return services.BuildServiceProvider();
    }

    private async Task<ImageCleanupResult> RunCleanupAsync(
        TimeProvider clock, StorageHooks? hooks = null, int? maxPerRun = null, bool dryRun = false)
    {
        await using var tools = Tools(clock, hooks, maxPerRun);
        await using var scope = tools.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IProductImageCleanup>()
            .RunAsync(Guid.CreateVersion7(), "ImageCleanupTest", dryRun, CancellationToken.None);
    }

    private static async Task<List<ListedObject>> ListAllAsync(IObjectStorage storage, string prefix)
    {
        var objects = new List<ListedObject>();
        string? token = null;
        do
        {
            var page = await storage.ListAsync(prefix, token, 100, CancellationToken.None);
            objects.AddRange(page.Objects);
            token = page.NextToken;
        }
        while (token is not null);

        return objects;
    }

    /// <summary>When the storage wrote each object of the products, by its own clock.</summary>
    private async Task<Dictionary<string, DateTimeOffset>> WrittenAsync(params Guid[] productIds)
    {
        await using var tools = Tools(TimeProvider.System);
        var storage = tools.GetRequiredService<IObjectStorage>();
        var written = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var productId in productIds)
        {
            foreach (var prefix in new[] { $"products/{productId}/", $"uploads/products/{productId}/" })
            {
                foreach (var listed in await ListAllAsync(storage, prefix))
                {
                    written[listed.Key] = listed.LastModified;
                }
            }
        }

        return written;
    }

    /// <summary>A PNG signature followed by filler, <paramref name="length"/> bytes in all.</summary>
    private static byte[] Image(int length)
    {
        var image = new byte[length];
        Random.Shared.NextBytes(image);
        PngSignature.AsSpan(0, Math.Min(length, PngSignature.Length)).CopyTo(image);
        return image;
    }

    /// <summary>A final object of the product that nothing refers to, as a confirmation that did not finish leaves it.</summary>
    private async Task<string> PutOrphanAsync(Guid productId)
    {
        var key = ProductImageKey.NewFinalFor(productId, Guid.CreateVersion7());
        await _storage.PutAsync(key, Image(64), ProductImageRules.Png);
        return key;
    }

    private async Task<ProductResponse> CreateProductAsync()
    {
        var response = await _manager.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest($"HYG-{TestData.UniqueSuffix()}".ToUpperInvariant(), "Product with image", 12.5m, 3, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<ProductResponse>();
    }

    private async Task<ProductResponse> GetAsync(Guid productId) =>
        (await _manager.GetFromJsonAsync<ProductResponse>($"/api/products/{productId}", TestData.Json))!;

    private Task<string?> ImageKeyAsync(Guid productId) =>
        factory.QueryDbAsync(db => db.Products.AsNoTracking().Where(p => p.ProductId == productId).Select(p => p.ImageKey).SingleAsync());

    /// <summary>Every object of the product in the bucket: its uploads (staging keys) and its final objects.</summary>
    private async Task<List<string>> ObjectsOfAsync(Guid productId) =>
    [
        .. (await _storage.KeysAsync($"products/{productId}/")).Concat(await _storage.KeysAsync($"uploads/products/{productId}/")).Order(StringComparer.Ordinal)
    ];

    /// <summary>Presign and upload as a browser does, not confirmed. Returns the staging key.</summary>
    private async Task<string> UploadNewAsync(Guid productId, byte[] content)
    {
        var presign = await _manager.PostAsJsonAsync(
            $"/api/products/{productId}/image/presign", new PresignProductImageRequest(ProductImageRules.Png, content.Length));
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var presigned = await presign.ReadAsync<PresignProductImageResponse>();
        using var request = new HttpRequestMessage(HttpMethod.Put, presigned.UploadUrl) { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ProductImageRules.Png);
        using var upload = await _browser.SendAsync(request);
        Assert.True(upload.IsSuccessStatusCode, $"upload: HTTP {(int)upload.StatusCode}");
        return presigned.ObjectKey;
    }

    private Task<HttpResponseMessage> ConfirmAsync(Guid productId, string key) =>
        _manager.PutAsJsonAsync($"/api/products/{productId}/image", new ConfirmProductImageRequest(key));

    private async Task<ProductImageResponse> SetImageAsync(Guid productId, byte[] content)
    {
        var confirm = await ConfirmAsync(productId, await UploadNewAsync(productId, content));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        return (await confirm.ReadAsync<ProductResponse>()).Image!;
    }

    private sealed class TestActor : ICurrentUser, ICorrelationContext
    {
        public string UserId => "system:image-hygiene-test";

        public string CorrelationId => "image-hygiene-test";
    }

    /// <summary>The real time, moved by the test.</summary>
    private sealed class TestClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>What a test does around the storage calls of the code under test.</summary>
    private sealed class StorageHooks
    {
        public Func<string, string, Task>? BeforeCopy { get; init; }

        public Func<string, string, Task>? AfterCopy { get; init; }

        public Func<string, StoredObjectPage, Task>? AfterList { get; init; }

        /// <summary>True for a key whose delete fails as when the storage does not answer.</summary>
        public Func<string, bool>? FailDelete { get; init; }

        public int Copies { get; set; }
    }

    private sealed class HookedStorage(IObjectStorage inner, StorageHooks hooks) : IObjectStorage
    {
        public PresignedUrl PresignPut(string key, string contentType, long contentLength, TimeSpan lifetime) =>
            inner.PresignPut(key, contentType, contentLength, lifetime);

        public PresignedUrl PresignGet(string key, TimeSpan lifetime) => inner.PresignGet(key, lifetime);

        public Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken) => inner.HeadAsync(key, cancellationToken);

        public Task<byte[]> ReadStartAsync(string key, int count, CancellationToken cancellationToken) => inner.ReadStartAsync(key, count, cancellationToken);

        public Task PutAsync(string key, Stream content, string contentType, string contentDisposition, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, contentType, contentDisposition, cancellationToken);

        public async Task<bool> CopyAsync(string sourceKey, string destinationKey, string contentType, CancellationToken cancellationToken)
        {
            if (hooks.BeforeCopy is not null)
            {
                await hooks.BeforeCopy(sourceKey, destinationKey);
            }

            hooks.Copies++;
            var copied = await inner.CopyAsync(sourceKey, destinationKey, contentType, cancellationToken);
            if (hooks.AfterCopy is not null)
            {
                await hooks.AfterCopy(sourceKey, destinationKey);
            }

            return copied;
        }

        public async Task<StoredObjectPage> ListAsync(string prefix, string? continuationToken, int maxKeys, CancellationToken cancellationToken)
        {
            var page = await inner.ListAsync(prefix, continuationToken, maxKeys, cancellationToken);
            if (hooks.AfterList is not null)
            {
                await hooks.AfterList(prefix, page);
            }

            return page;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
            hooks.FailDelete?.Invoke(key) == true ? throw new ObjectStorageException("delete") : inner.DeleteAsync(key, cancellationToken);
    }
}
