using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;

namespace Omnichannel.Infrastructure.S3;

/// <summary>
/// <see cref="IObjectStorage"/> over the S3 API (<c>AWSSDK.S3</c> as client library only: endpoint, addressing style and
/// keys are configuration, see <see cref="ObjectStorageOptions"/>). One client for the process; it is thread-safe.
/// <para>
/// A failure of the storage becomes <see cref="ObjectStorageException"/> and one warning that names the operation and
/// the kind of failure (HTTP status and S3 error code, or the exception type) - never the exception itself, whose text
/// can carry the endpoint. Links and credentials are never logged.
/// </para>
/// </summary>
internal sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly Protocol _protocol;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<S3ObjectStorage> _logger;

    public S3ObjectStorage(IOptions<ObjectStorageOptions> options, TimeProvider timeProvider, ILogger<S3ObjectStorage> logger)
    {
        var settings = options.Value;
        var useHttp = settings.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        _client = new AmazonS3Client(
            new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = settings.ServiceUrl,
                ForcePathStyle = settings.ForcePathStyle,
                AuthenticationRegion = settings.Region,
                UseHttp = useHttp,
                Timeout = settings.RequestTimeout,
                // The caller is a user waiting for an answer: one more try, then 503.
                MaxErrorRetry = 1
            });
        _bucket = settings.BucketName;
        _protocol = useHttp ? Protocol.HTTP : Protocol.HTTPS;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public PresignedUrl PresignPut(string key, string contentType, TimeSpan lifetime) =>
        Presign(HttpVerb.PUT, key, lifetime, contentType);

    public PresignedUrl PresignGet(string key, TimeSpan lifetime) => Presign(HttpVerb.GET, key, lifetime, contentType: null);

    public async Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var head = await _client.GetObjectMetadataAsync(_bucket, key, cancellationToken);
            return new StoredObject(head.ContentLength, head.Headers.ContentType);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception exception) when (IsStorageFailure(exception, cancellationToken))
        {
            throw Failed("head", exception);
        }
    }

    public async Task<byte[]> ReadStartAsync(string key, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        try
        {
            using var response = await _client.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = key, ByteRange = new ByteRange(0, count - 1) },
                cancellationToken);

            var buffer = new byte[count];
            var read = await response.ResponseStream.ReadAtLeastAsync(buffer, count, throwOnEndOfStream: false, cancellationToken);
            return buffer[..read];
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // An empty object has no first byte.
            return [];
        }
        catch (Exception exception) when (IsStorageFailure(exception, cancellationToken))
        {
            throw Failed("read", exception);
        }
    }

    public async Task PutAsync(string key, Stream content, string contentType, string contentDisposition, CancellationToken cancellationToken)
    {
        if (!content.CanSeek)
        {
            // The one retry of a failed request sends the content again from where it started.
            throw new ArgumentException("The content must be a seekable stream.", nameof(content));
        }

        try
        {
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false
            };
            request.Headers.ContentDisposition = contentDisposition;
            await _client.PutObjectAsync(request, cancellationToken);
        }
        catch (Exception exception) when (IsStorageFailure(exception, cancellationToken))
        {
            throw Failed("put", exception);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            // S3 answers 204 for a key without an object as well.
            await _client.DeleteObjectAsync(_bucket, key, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
        catch (Exception exception) when (IsStorageFailure(exception, cancellationToken))
        {
            throw Failed("delete", exception);
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Computed locally from the secret key: no request is sent.</summary>
    private PresignedUrl Presign(HttpVerb verb, string key, TimeSpan lifetime, string? contentType)
    {
        var expiresAt = _timeProvider.GetUtcNow().Add(lifetime);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = verb,
            Expires = expiresAt.UtcDateTime,
            Protocol = _protocol
        };
        if (contentType is not null)
        {
            // Signed: the upload must carry exactly this Content-Type.
            request.ContentType = contentType;
        }

        return new PresignedUrl(_client.GetPreSignedURL(request), expiresAt);
    }

    /// <summary>
    /// The storage is unreachable, slow, or refused the request - not the caller giving up, and not a programming error.
    /// </summary>
    private static bool IsStorageFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is AmazonServiceException or AmazonClientException or HttpRequestException or IOException or TimeoutException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private ObjectStorageException Failed(string operation, Exception exception)
    {
        var failure = exception is AmazonServiceException service && service.StatusCode != 0
            ? $"HTTP {(int)service.StatusCode} {service.ErrorCode}"
            : exception.GetType().Name;
        _logger.LogWarning("Object storage {Operation} failed: {Failure}", operation, failure);
        return new ObjectStorageException(operation);
    }
}

/// <summary>
/// The storage of a host that has none (one that does not call <c>AddObjectStorage</c>; the API and the Workers host
/// both do). Products without an image never reach it; anything that needs the storage fails loudly instead of
/// answering wrongly.
/// </summary>
internal sealed class UnconfiguredObjectStorage : IObjectStorage
{
    public PresignedUrl PresignPut(string key, string contentType, TimeSpan lifetime) => throw NotConfigured();

    public PresignedUrl PresignGet(string key, TimeSpan lifetime) => throw NotConfigured();

    public Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken) => throw NotConfigured();

    public Task<byte[]> ReadStartAsync(string key, int count, CancellationToken cancellationToken) => throw NotConfigured();

    public Task PutAsync(string key, Stream content, string contentType, string contentDisposition, CancellationToken cancellationToken) =>
        throw NotConfigured();

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw NotConfigured();

    private static InvalidOperationException NotConfigured() =>
        new("This host has no object storage: call AddObjectStorage() and configure the ObjectStorage section.");
}
