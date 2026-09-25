namespace Omnichannel.Application.Common.Exceptions;

/// <summary>Requested resource does not exist. Mapped to HTTP 404.</summary>
public sealed class NotFoundException(string resource, object key)
    : Exception($"{resource} '{key}' was not found.")
{
    public string Resource { get; } = resource;
    public object Key { get; } = key;
}
