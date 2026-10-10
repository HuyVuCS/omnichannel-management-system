using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Infrastructure.S3;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 025 C2: <c>ObjectStorage:PublicServiceUrl</c>. Presigned links are signed for the origin the browsers reach;
/// the requests of the host itself keep going to <c>ServiceUrl</c>. The storage of the tests listens on one port and
/// is reached under two host names - <c>127.0.0.1</c> ("inside") and <c>localhost</c> ("public") - which is exactly the
/// situation behind a reverse proxy that keeps the Host header: the signature covers the host.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StoragePublicOriginTests(ApiFactory factory)
{
    private static readonly byte[] Content = [1, 2, 3, 4, 5, 6, 7];

    private static ServiceProvider StorageWith(StorageEmulator emulator, string? publicServiceUrl)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ObjectStorage:ServiceUrl"] = emulator.ServiceUrl,
            ["ObjectStorage:PublicServiceUrl"] = publicServiceUrl,
            ["ObjectStorage:Region"] = StorageEmulator.Region,
            ["ObjectStorage:ForcePathStyle"] = "true",
            ["ObjectStorage:BucketName"] = StorageEmulator.Bucket,
            ["ObjectStorage:AccessKeyId"] = emulator.AccessKeyId,
            ["ObjectStorage:SecretAccessKey"] = emulator.SecretAccessKey
        };
        return new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddObjectStorage()
            .BuildServiceProvider();
    }

    private static string PublicUrlOf(StorageEmulator emulator) =>
        new UriBuilder(emulator.ServiceUrl) { Host = "localhost" }.Uri.GetLeftPart(UriPartial.Authority);

    [Fact]
    public async Task Links_are_signed_for_the_public_origin_and_work_there_while_the_host_s_own_calls_use_the_service_url()
    {
        var emulator = await factory.StorageAsync();
        var publicUrl = PublicUrlOf(emulator);
        await using var services = StorageWith(emulator, publicUrl);
        var storage = services.GetRequiredService<IObjectStorage>();
        var key = $"public-origin/{Guid.NewGuid():N}";
        using var browser = new HttpClient();

        // Upload through the public origin, as a browser does.
        var upload = storage.PresignPut(key, "application/octet-stream", TimeSpan.FromMinutes(1));
        Assert.StartsWith($"{publicUrl}/", upload.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", upload.Url, StringComparison.Ordinal);
        using var body = new ByteArrayContent(Content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        Assert.Equal(HttpStatusCode.OK, (await browser.PutAsync(upload.Url, body)).StatusCode);

        // The host's own calls (HEAD, ranged GET) reach the same object through the service URL.
        Assert.Equal(Content.Length, (await storage.HeadAsync(key, CancellationToken.None))!.Length);
        Assert.Equal(Content[..3], await storage.ReadStartAsync(key, 3, CancellationToken.None));

        // Download through the public origin.
        var download = storage.PresignGet(key, TimeSpan.FromMinutes(1));
        Assert.StartsWith($"{publicUrl}/", download.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", download.Url, StringComparison.Ordinal);
        Assert.Equal(Content, await browser.GetByteArrayAsync(download.Url));

        await storage.DeleteAsync(key, CancellationToken.None);
        Assert.Null(await storage.HeadAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task A_link_is_valid_for_the_host_it_was_signed_for_only()
    {
        var emulator = await factory.StorageAsync();
        await using var services = StorageWith(emulator, PublicUrlOf(emulator));
        var storage = services.GetRequiredService<IObjectStorage>();
        var key = $"public-origin/{Guid.NewGuid():N}";
        using var stream = new MemoryStream(Content);
        await storage.PutAsync(key, stream, "application/octet-stream", "attachment", CancellationToken.None);
        using var browser = new HttpClient();

        // The same link with the internal host instead: the signature does not cover it. This is why the link has to be
        // signed for the public origin and cannot be rewritten by a proxy.
        var download = storage.PresignGet(key, TimeSpan.FromMinutes(1));
        var rewritten = new UriBuilder(download.Url) { Host = "127.0.0.1" }.Uri;

        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync(rewritten)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(download.Url)).StatusCode);

        await storage.DeleteAsync(key, CancellationToken.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Without_a_public_origin_links_are_built_on_the_service_url_as_before(string? publicServiceUrl)
    {
        var emulator = await factory.StorageAsync();
        await using var services = StorageWith(emulator, publicServiceUrl);
        var storage = services.GetRequiredService<IObjectStorage>();

        Assert.StartsWith($"{emulator.ServiceUrl}/", storage.PresignGet("any/key", TimeSpan.FromMinutes(1)).Url, StringComparison.Ordinal);
        Assert.StartsWith(
            $"{emulator.ServiceUrl}/", storage.PresignPut("any/key", "image/png", TimeSpan.FromMinutes(1)).Url, StringComparison.Ordinal);
    }

    [Fact]
    public void A_public_origin_with_https_gives_https_links_whatever_the_scheme_inside_the_network()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ObjectStorage:ServiceUrl"] = "http://objectstorage:8333",
            ["ObjectStorage:PublicServiceUrl"] = "https://files.example.test",
            ["ObjectStorage:BucketName"] = "bucket",
            ["ObjectStorage:AccessKeyId"] = "key-id",
            ["ObjectStorage:SecretAccessKey"] = "secret"
        };
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddObjectStorage()
            .BuildServiceProvider();
        var storage = services.GetRequiredService<IObjectStorage>();

        // Computed locally: nothing is sent, so no storage is needed.
        foreach (var link in new[]
                 {
                     storage.PresignGet("products/p/image", TimeSpan.FromMinutes(1)).Url,
                     storage.PresignPut("products/p/image", "image/png", TimeSpan.FromMinutes(1)).Url
                 })
        {
            Assert.StartsWith("https://files.example.test/bucket/products/p/image?", link, StringComparison.Ordinal);
            Assert.DoesNotContain("objectstorage", link, StringComparison.Ordinal);
            Assert.DoesNotContain("8333", link, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("files.example.test")]
    [InlineData("ftp://files.example.test")]
    [InlineData("/storage")]
    [InlineData("https://key:secret-in-url@files.example.test")]
    public void A_public_origin_that_is_no_http_url_is_one_failure_that_names_the_setting_and_no_value(string value)
    {
        var options = new ObjectStorageOptions
        {
            ServiceUrl = "http://objectstorage:8333",
            PublicServiceUrl = value,
            BucketName = "bucket",
            AccessKeyId = "key-id",
            SecretAccessKey = "secret"
        };

        var result = new ObjectStorageOptionsValidator().Validate(null, options);

        var failure = Assert.Single(result.Failures!);
        Assert.StartsWith("ObjectStorage:PublicServiceUrl ", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-in-url", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://files.example.test")]
    [InlineData("http://localhost:8081")]
    public void No_public_origin_or_an_http_url_is_valid(string? value)
    {
        var options = new ObjectStorageOptions
        {
            ServiceUrl = "http://objectstorage:8333",
            PublicServiceUrl = value,
            BucketName = "bucket",
            AccessKeyId = "key-id",
            SecretAccessKey = "secret"
        };

        Assert.True(new ObjectStorageOptionsValidator().Validate(null, options).Succeeded);
        Assert.IsAssignableFrom<IValidateOptions<ObjectStorageOptions>>(new ObjectStorageOptionsValidator());
    }
}
