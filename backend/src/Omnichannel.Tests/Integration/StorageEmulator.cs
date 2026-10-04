using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// The S3-compatible storage of the integration tests: the emulator of development (SeaweedFS, same image and flags as
/// <c>docker/docker-compose.yml</c>) in a container.
/// <para>
/// Its address and keys are fixed when the object is created - before the API host is built, which only needs them as
/// configuration - and the container is started by the first test that needs it (<see cref="ApiFactory.StorageAsync"/>).
/// Tests that never touch an image run without it. The host port is fixed too, so the container can be stopped and
/// started again ("storage unreachable") under the same address.
/// </para>
/// </summary>
public sealed class StorageEmulator : IAsyncDisposable
{
    public const string Image = "chrislusf/seaweedfs:4.48";
    public const string Bucket = "omnichannel-test";
    public const string Region = "us-east-1";

    /// <summary>
    /// The origins the bucket's CORS allows, as the development compose file does: the dev server and <c>vite preview</c>,
    /// each under both names of the local machine.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedOrigins =
        ["http://localhost:5173", "http://localhost:4173", "http://127.0.0.1:5173", "http://127.0.0.1:4173"];

    private const int S3Port = 8333;

    private readonly IContainer _container;
    private readonly AmazonS3Client _client;

    public StorageEmulator()
    {
        var hostPort = FreePort();
        ServiceUrl = $"http://127.0.0.1:{hostPort}";
        // Test-only credentials, generated per run.
        AccessKeyId = RandomNumberGenerator.GetHexString(20);
        SecretAccessKey = RandomNumberGenerator.GetHexString(40, lowercase: true);

        _container = new ContainerBuilder(Image)
            .WithCommand(
                "mini", "-dir=/data", $"-bucket={Bucket}", $"-s3.allowedOrigins={string.Join(',', AllowedOrigins)}",
                "-admin.ui=false", "-webdav=false", "-s3.port.iceberg=0")
            .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKeyId)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretAccessKey)
            .WithExposedPort(S3Port)
            // Loopback only, like the compose file.
            .WithCreateParameterModifier(parameters => (parameters.HostConfig ??= new HostConfig()).PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                [$"{S3Port}/tcp"] = [new PortBinding { HostIP = "127.0.0.1", HostPort = hostPort.ToString() }]
            })
            .Build();

        _client = new AmazonS3Client(
            new BasicAWSCredentials(AccessKeyId, SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = ServiceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = Region,
                UseHttp = true,
                Timeout = TimeSpan.FromSeconds(10),
                MaxErrorRetry = 0
            });
    }

    public string ServiceUrl { get; }

    public string AccessKeyId { get; }

    public string SecretAccessKey { get; }

    /// <summary>Starts the container (again) and returns when the bucket answers signed requests.</summary>
    public async Task StartAsync()
    {
        await _container.StartAsync();
        await Eventually.WaitAsync(
            async () =>
            {
                try
                {
                    await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, MaxKeys = 1 });
                    return true;
                }
                catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException or IOException)
                {
                    return false;
                }
            },
            ready => ready,
            TimeSpan.FromSeconds(60),
            "the storage emulator answers for its bucket");
    }

    /// <summary>The storage is gone: connections to its address are refused until <see cref="StartAsync"/>.</summary>
    public Task StopAsync() => _container.StopAsync();

    public async Task<bool> ExistsAsync(string key)
    {
        try
        {
            await _client.GetObjectMetadataAsync(Bucket, key);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>Stores an object directly, with any content type - what a presigned link of the API would never allow.</summary>
    public async Task PutAsync(string key, byte[] content, string contentType)
    {
        using var stream = new MemoryStream(content);
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, InputStream = stream, ContentType = contentType });
    }

    /// <summary>Keys under <paramref name="prefix"/>, e.g. every object of one product.</summary>
    public async Task<IReadOnlyList<string>> KeysAsync(string prefix)
    {
        var listed = await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Prefix = prefix });
        return (listed.S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _container.DisposeAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
