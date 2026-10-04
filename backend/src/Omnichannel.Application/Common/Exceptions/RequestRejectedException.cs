namespace Omnichannel.Application.Common.Exceptions;

/// <summary>
/// The request is well-formed but asks for something the application does not do (e.g. more rows than an export may
/// hold). Mapped to HTTP 400 with <paramref name="errorCode"/> - unlike a validation failure, it belongs to no field.
/// </summary>
public sealed class RequestRejectedException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
