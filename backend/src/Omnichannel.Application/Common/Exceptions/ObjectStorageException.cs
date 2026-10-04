namespace Omnichannel.Application.Common.Exceptions;

/// <summary>
/// The object storage did not answer, or refused the application's request. Mapped to HTTP 503
/// <c>storage_unavailable</c>. Carries the name of the operation only: no endpoint, no key id, no link and no text
/// of the storage - the implementation logs what an operator needs.
/// </summary>
public sealed class ObjectStorageException(string operation)
    : Exception($"Object storage operation '{operation}' failed.")
{
    public const string ErrorCode = "storage_unavailable";

    public string Operation { get; } = operation;
}
