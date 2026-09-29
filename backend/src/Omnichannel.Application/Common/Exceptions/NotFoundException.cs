namespace Omnichannel.Application.Common.Exceptions;

/// <summary>
/// Requested resource does not exist. Mapped to HTTP 404.
/// <paramref name="errorCode"/> is a stable, machine-readable code for the client (default <c>not_found</c>).
/// </summary>
public sealed class NotFoundException(string resource, object key, string errorCode = NotFoundException.DefaultErrorCode)
    : Exception($"{resource} '{key}' was not found.")
{
    public const string DefaultErrorCode = "not_found";

    public string Resource { get; } = resource;
    public object Key { get; } = key;
    public string ErrorCode { get; } = errorCode;
}
