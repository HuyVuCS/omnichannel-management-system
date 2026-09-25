using Serilog.Context;

namespace Omnichannel.Api.Middleware;

/// <summary>
/// Accepts an incoming X-Correlation-Id (if well-formed) or creates one, echoes it on the
/// response and pushes it to the Serilog LogContext so every log line of the request carries it.
/// The same id is later propagated into RabbitMQ envelopes and processing tasks.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const int MaxLength = 100;
    internal const string ItemKey = "Omnichannel.CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Resolve(context.Request.Headers[HeaderName].ToString());

        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    /// <summary>
    /// Client-supplied values are only trusted if short and made of safe characters,
    /// which prevents log forging / header injection through the correlation id.
    /// </summary>
    public static string Resolve(string? candidate)
    {
        return IsValid(candidate) ? candidate! : Guid.NewGuid().ToString("N");
    }

    public static bool IsValid(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && value.Length <= MaxLength
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }
}

public static class CorrelationIdHttpContextExtensions
{
    public static string? GetCorrelationId(this HttpContext context)
    {
        return context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var value) ? value as string : null;
    }
}
