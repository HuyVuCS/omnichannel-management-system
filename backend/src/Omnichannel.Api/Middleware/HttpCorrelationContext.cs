using Omnichannel.Application.Common.Abstractions;

namespace Omnichannel.Api.Middleware;

/// <summary>Correlation id resolved by <see cref="CorrelationIdMiddleware"/> for the current request.</summary>
public sealed class HttpCorrelationContext(IHttpContextAccessor httpContextAccessor) : ICorrelationContext
{
    // Fallback only outside a request (should not happen for API code paths); cached so it is stable per scope.
    private readonly Lazy<string> _fallback = new(() => Guid.NewGuid().ToString("N"));

    public string CorrelationId => httpContextAccessor.HttpContext?.GetCorrelationId() ?? _fallback.Value;
}
