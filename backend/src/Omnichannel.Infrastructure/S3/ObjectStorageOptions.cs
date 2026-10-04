using Microsoft.Extensions.Options;

namespace Omnichannel.Infrastructure.S3;

/// <summary>
/// Bound from the <c>ObjectStorage</c> configuration section: any S3-compatible storage reached through the S3 API (a
/// local emulator in development, a hosted provider otherwise). Nothing here is specific to one provider.
/// </summary>
public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    /// <summary>
    /// Endpoint of the S3 API, e.g. <c>http://127.0.0.1:8333</c>. Presigned links are built on it, so it must be the address
    /// the browsers of the users reach as well.
    /// </summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>Region the requests are signed for. Emulators and most S3-compatible providers accept any; some name their own.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary><c>{endpoint}/{bucket}/{key}</c> instead of <c>{bucket}.{endpoint}/{key}</c>. Emulators need it.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>The private bucket. It must exist; the application never creates it.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>Supply via environment variable <c>ObjectStorage__AccessKeyId</c> or user-secrets, never appsettings.</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>Secret. Supply via environment variable <c>ObjectStorage__SecretAccessKey</c> or user-secrets, never appsettings.</summary>
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>How long a presigned upload link is valid.</summary>
    public TimeSpan UploadUrlLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a presigned download link (the <c>image.url</c> of a product) is valid.</summary>
    public TimeSpan DownloadUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Largest product image, in bytes (default 5 MiB).</summary>
    public long MaxImageBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Time one request to the storage may take (a failed request is tried once more).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Presence and ranges only: start-up never connects to the storage.</summary>
public sealed class ObjectStorageOptionsValidator : IValidateOptions<ObjectStorageOptions>
{
    public static readonly TimeSpan MinUrlLifetime = TimeSpan.FromSeconds(1);

    /// <summary>The longest lifetime a SigV4 presigned link can have.</summary>
    public static readonly TimeSpan MaxUrlLifetime = TimeSpan.FromDays(7);

    public const long MaxImageBytesLimit = 100L * 1024 * 1024;

    public static readonly TimeSpan MinRequestTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromMinutes(2);

    public ValidateOptionsResult Validate(string? name, ObjectStorageOptions options)
    {
        var failures = new List<string>();

        if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            // Never include the value itself: a mistyped one may carry credentials.
            failures.Add("ObjectStorage:ServiceUrl must be an absolute http or https URL without credentials.");
        }

        if (string.IsNullOrWhiteSpace(options.Region))
        {
            failures.Add("ObjectStorage:Region is required.");
        }

        if (string.IsNullOrWhiteSpace(options.BucketName))
        {
            failures.Add("ObjectStorage:BucketName is required.");
        }

        if (string.IsNullOrWhiteSpace(options.AccessKeyId))
        {
            failures.Add("ObjectStorage:AccessKeyId is required (environment variable or user-secrets).");
        }

        if (string.IsNullOrEmpty(options.SecretAccessKey))
        {
            failures.Add("ObjectStorage:SecretAccessKey is required (environment variable or user-secrets).");
        }

        if (options.UploadUrlLifetime < MinUrlLifetime || options.UploadUrlLifetime > MaxUrlLifetime)
        {
            failures.Add($"ObjectStorage:UploadUrlLifetime must be between {MinUrlLifetime:c} and {MaxUrlLifetime:c}.");
        }

        if (options.DownloadUrlLifetime < MinUrlLifetime || options.DownloadUrlLifetime > MaxUrlLifetime)
        {
            failures.Add($"ObjectStorage:DownloadUrlLifetime must be between {MinUrlLifetime:c} and {MaxUrlLifetime:c}.");
        }

        if (options.MaxImageBytes is < 1 or > MaxImageBytesLimit)
        {
            failures.Add($"ObjectStorage:MaxImageBytes must be between 1 and {MaxImageBytesLimit}.");
        }

        if (options.RequestTimeout < MinRequestTimeout || options.RequestTimeout > MaxRequestTimeout)
        {
            failures.Add($"ObjectStorage:RequestTimeout must be between {MinRequestTimeout:c} and {MaxRequestTimeout:c}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
