namespace Omnichannel.Application.Common.Exceptions;

/// <summary>
/// Request conflicts with current state (duplicate key, stale version...). Mapped to HTTP 409.
/// <paramref name="errorCode"/> is a stable, machine-readable code for the client.
/// </summary>
public sealed class ConflictException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
