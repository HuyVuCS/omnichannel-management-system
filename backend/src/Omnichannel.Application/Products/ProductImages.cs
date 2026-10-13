namespace Omnichannel.Application.Products;

/// <summary>
/// The image of a product as a client gets it. <paramref name="Url"/> reads the object from the storage without the
/// API's token until <paramref name="ExpiresAt"/>; it is computed for every response, so it differs between two
/// responses for the same image - <paramref name="UpdatedAt"/> is what identifies the image.
/// </summary>
public sealed record ProductImageResponse(
    string Url,
    DateTimeOffset ExpiresAt,
    string ContentType,
    long SizeBytes,
    DateTimeOffset UpdatedAt);

/// <summary>What the client is about to upload.</summary>
public sealed record PresignProductImageRequest(string ContentType, long SizeBytes);

/// <summary>
/// Where and how to upload: <paramref name="Method"/> <paramref name="UploadUrl"/> with exactly
/// <paramref name="Headers"/>, without the API's token, before <paramref name="ExpiresAt"/>. Afterwards the upload is
/// confirmed with <paramref name="ObjectKey"/>.
/// </summary>
public sealed record PresignProductImageResponse(
    string ObjectKey,
    string UploadUrl,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt);

/// <summary>The key a presign answer named (a staging key, <see cref="ProductImageKey"/>), after the upload.</summary>
public sealed record ConfirmProductImageRequest(string ObjectKey);

/// <summary>What a product image may be. The server is the authority; clients repeat the check only to save a request.</summary>
public static class ProductImageRules
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string WebP = "image/webp";

    /// <summary>No SVG (it can carry script, and the storage serves what is stored) and no GIF.</summary>
    public static readonly IReadOnlyList<string> AllowedContentTypes = [Jpeg, Png, WebP];

    /// <summary>How many bytes from the start of an object <see cref="StartsLike"/> needs.</summary>
    public const int SignatureLength = 12;

    /// <summary>
    /// The longest time between the moment a confirmation copies an upload to its final key and the moment it locks the
    /// product to refer to that copy; a confirmation that needed longer (a storage that barely answers, a long wait for
    /// the row) gives up and removes its copy. The image clean-up relies on it: a copy much older than this that no
    /// product refers to never will be (<see cref="ImageCleanupOptionsRules.MinMinAge"/>).
    /// </summary>
    public static readonly TimeSpan ConfirmationDeadline = TimeSpan.FromSeconds(30);

    public static bool IsAllowed(string? contentType) =>
        contentType is not null && AllowedContentTypes.Contains(contentType, StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="start"/> - the first bytes of an object - begins with the signature of
    /// <paramref name="contentType"/>: the content is what it is declared to be, as far as its first bytes can tell.
    /// </summary>
    public static bool StartsLike(string contentType, ReadOnlySpan<byte> start) => contentType switch
    {
        Jpeg => start.StartsWith<byte>([0xFF, 0xD8, 0xFF]),
        Png => start.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        // "RIFF", four bytes of length, "WEBP".
        WebP => start.Length >= SignatureLength && start[..4].SequenceEqual("RIFF"u8) && start[8..12].SequenceEqual("WEBP"u8),
        _ => false
    };
}

