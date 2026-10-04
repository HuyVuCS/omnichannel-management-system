namespace Omnichannel.Application.Common.Abstractions;

/// <summary>
/// The private bucket of the application in an S3-compatible object storage. Only what the application needs today:
/// links a browser can use without the API's token, and the checks of an uploaded object.
/// <para>
/// The two <c>Presign…</c> methods compute a link locally and never call the storage. The others do, and fail with
/// <see cref="Exceptions.ObjectStorageException"/> when the storage does not answer or refuses - never call them
/// inside a database transaction.
/// </para>
/// A presigned link is a credential until it expires: it is returned to the caller and never logged.
/// </summary>
public interface IObjectStorage
{
    /// <summary>
    /// A link that stores an object under <paramref name="key"/> with <c>PUT</c>. The request must carry exactly
    /// <paramref name="contentType"/> as <c>Content-Type</c>; the storage rejects any other. Size and content are not
    /// bound by the link: check the object afterwards.
    /// </summary>
    PresignedUrl PresignPut(string key, string contentType, TimeSpan lifetime);

    /// <summary>A link that reads the object with <c>GET</c>.</summary>
    PresignedUrl PresignGet(string key, TimeSpan lifetime);

    /// <summary>Length and content type of the object; null when there is no object under <paramref name="key"/>.</summary>
    Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken);

    /// <summary>The first <paramref name="count"/> bytes of the object (fewer when it is shorter).</summary>
    Task<byte[]> ReadStartAsync(string key, int count, CancellationToken cancellationToken);

    /// <summary>Deletes the object. Succeeds when there is none.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public sealed record PresignedUrl(string Url, DateTimeOffset ExpiresAt);

public sealed record StoredObject(long Length, string? ContentType);
