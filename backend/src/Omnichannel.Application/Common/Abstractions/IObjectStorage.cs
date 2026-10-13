namespace Omnichannel.Application.Common.Abstractions;

/// <summary>
/// The private bucket of the application in an S3-compatible object storage. Only what the application needs today:
/// links a browser can use without the API's token, the checks of an uploaded object, a copy inside the bucket,
/// a listing for the clean-up, and storing a file the application made itself (an export).
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
    /// <paramref name="contentType"/> as <c>Content-Type</c> and a body of exactly <paramref name="contentLength"/>
    /// bytes: both headers are signed (a browser sets <c>Content-Length</c> itself), the storage rejects any other.
    /// A storage that ignores a signed length is possible, and the content is never bound by the link: check the
    /// object afterwards.
    /// </summary>
    PresignedUrl PresignPut(string key, string contentType, long contentLength, TimeSpan lifetime);

    /// <summary>A link that reads the object with <c>GET</c>.</summary>
    PresignedUrl PresignGet(string key, TimeSpan lifetime);

    /// <summary>Length and content type of the object; null when there is no object under <paramref name="key"/>.</summary>
    Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken);

    /// <summary>The first <paramref name="count"/> bytes of the object (fewer when it is shorter).</summary>
    Task<byte[]> ReadStartAsync(string key, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="content"/> (a seekable stream, read from its current position) under
    /// <paramref name="key"/>, replacing an object that is there. The object is served with
    /// <paramref name="contentType"/> and <paramref name="contentDisposition"/> as its <c>Content-Type</c> and
    /// <c>Content-Disposition</c>. The stream is left open.
    /// </summary>
    Task PutAsync(string key, Stream content, string contentType, string contentDisposition, CancellationToken cancellationToken);

    /// <summary>
    /// Copies the object under <paramref name="sourceKey"/> to <paramref name="destinationKey"/> inside the storage
    /// (no bytes pass through this host), replacing an object that is there. The copy is served with
    /// <paramref name="contentType"/>, whatever the source was stored with, and counts as written now. False when
    /// there is no object under <paramref name="sourceKey"/>.
    /// </summary>
    Task<bool> CopyAsync(string sourceKey, string destinationKey, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the objects whose key starts with <paramref name="prefix"/>, in the order of the storage, at most
    /// <paramref name="maxKeys"/>. <paramref name="continuationToken"/> is the <see cref="StoredObjectPage.NextToken"/>
    /// of the page before; null starts at the beginning.
    /// </summary>
    Task<StoredObjectPage> ListAsync(string prefix, string? continuationToken, int maxKeys, CancellationToken cancellationToken);

    /// <summary>Deletes the object. Succeeds when there is none.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>An object of a listing. <paramref name="LastModified"/> is when the storage wrote it (upload or copy).</summary>
public sealed record ListedObject(string Key, long Length, DateTimeOffset LastModified);

/// <param name="NextToken">Null when this was the last page.</param>
public sealed record StoredObjectPage(IReadOnlyList<ListedObject> Objects, string? NextToken);

public sealed record PresignedUrl(string Url, DateTimeOffset ExpiresAt);

public sealed record StoredObject(long Length, string? ContentType);
