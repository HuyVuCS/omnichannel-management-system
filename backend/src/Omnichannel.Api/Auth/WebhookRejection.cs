using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Omnichannel.Application.Orders;

namespace Omnichannel.Api.Auth;

/// <summary><c>errorCode</c> values of a webhook that is refused before its body is read as an order.</summary>
public static class WebhookErrorCodes
{
    /// <summary>429: more webhooks from this client address for this channel than allowed per minute.</summary>
    public const string RateLimited = "webhook_rate_limited";

    /// <summary>413: the body is longer than <c>Webhooks:MaxBodyBytes</c>.</summary>
    public const string BodyTooLarge = "webhook_body_too_large";

    /// <summary>404: no Active channel with this slug that has a webhook secret (unknown, inactive and unconfigured look the same).</summary>
    public const string ChannelNotFound = OrderErrorCodes.ChannelNotFound;

    /// <summary>401: <c>X-Webhook-Timestamp</c> is missing, not a number of seconds, or too far from the clock of the API.</summary>
    public const string TimestampInvalid = "webhook_timestamp_invalid";

    /// <summary>401: <c>X-Webhook-Signature</c> is missing, has no <c>v1</c> value, or none that matches.</summary>
    public const string SignatureInvalid = "webhook_signature_invalid";
}

/// <summary>
/// The answer and the log line of a refused webhook. The answer is a ProblemDetails with <c>errorCode</c> like every error
/// of the API. The log line is one Warning with the channel slug, the reason and the client address - and nothing the
/// request carried: no body, no header value, no signature (a refused webhook is unauthenticated input).
/// </summary>
public static class WebhookRejection
{
    private const string ProblemContentType = "application/problem+json";

    public static ProblemDetails Problem(HttpContext httpContext, int statusCode, string errorCode)
    {
        var (title, detail) = errorCode switch
        {
            WebhookErrorCodes.RateLimited => ("Too many requests", "Too many webhooks from this address for this channel. Try again in a minute."),
            WebhookErrorCodes.BodyTooLarge => ("Request body too large", "The body of the webhook is larger than this API accepts."),
            WebhookErrorCodes.ChannelNotFound => ("Resource not found", "There is no webhook for this channel."),
            WebhookErrorCodes.TimestampInvalid => (
                "Webhook not authenticated",
                $"The {WebhookSignature.TimestampHeader} header is missing, is not a number of seconds, or is too far from the current time."),
            _ => (
                "Webhook not authenticated",
                $"The {WebhookSignature.SignatureHeader} header is missing or does not match the body and the timestamp.")
        };

        var problem = httpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>()
            .CreateProblemDetails(httpContext, statusCode, title, detail: detail);
        problem.Extensions["errorCode"] = errorCode;
        return problem;
    }

    /// <summary>The answer as the result of an MVC filter.</summary>
    public static IActionResult Result(HttpContext httpContext, int statusCode, string errorCode) =>
        new ObjectResult(Problem(httpContext, statusCode, errorCode))
        {
            StatusCode = statusCode,
            ContentTypes = { ProblemContentType }
        };

    /// <summary>The answer written directly (outside MVC: the rate limiter).</summary>
    public static Task WriteAsync(HttpContext httpContext, int statusCode, string errorCode, CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = statusCode;
        return httpContext.Response.WriteAsJsonAsync(
            Problem(httpContext, statusCode, errorCode), options: null, ProblemContentType, cancellationToken);
    }

    public static void Log(ILogger logger, HttpContext httpContext, int statusCode, string errorCode) =>
        logger.LogWarning(
            "Webhook refused with {StatusCode} {ReasonCode}: channel slug {ChannelSlug}, client {ClientIp}",
            statusCode,
            errorCode,
            WebhookCaller.SlugForLog(httpContext.GetRouteValue(SlugRouteValue) as string),
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    /// <summary>Name of the route value that holds the channel slug of a webhook.</summary>
    public const string SlugRouteValue = "slug";
}
