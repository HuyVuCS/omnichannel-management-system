using System.Buffers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Orders;

namespace Omnichannel.Api.Filters;

/// <summary>
/// What stands between the address of a webhook and its action. A resource filter: it runs after routing and the rate
/// limiter and <b>before model binding</b>, so nothing of the body is parsed before the request is authenticated. In
/// this order, each step ending the request when it fails:
/// <list type="number">
/// <item>the raw body is read, at most <c>Webhooks:MaxBodyBytes</c> → 413;</item>
/// <item>the slug is an Active channel that has a webhook secret → 404 (unknown, inactive and unconfigured look the same);</item>
/// <item>the timestamp is a number of seconds close to the clock → 401; the signature matches the timestamp and the
/// body bytes with one of the channel's secrets → 401 (<see cref="WebhookSignature"/>);</item>
/// <item>the request becomes <c>webhook:{slug}</c> (<see cref="WebhookCaller"/>), and model binding gets the very bytes
/// the signature was checked against.</item>
/// </list>
/// </summary>
public sealed class WebhookSignatureFilter(
    IOptions<WebhookOptions> options,
    IWebhookOrderService webhookOrders,
    TimeProvider timeProvider,
    ILogger<WebhookSignatureFilter> logger) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var settings = options.Value;
        var slug = WebhookCaller.NormaliseSlug(context.RouteData.Values[WebhookRejection.SlugRouteValue] as string);

        var body = await ReadBodyAsync(httpContext.Request, settings.MaxBodyBytes, httpContext.RequestAborted);
        if (body is null)
        {
            Refuse(context, StatusCodes.Status413PayloadTooLarge, WebhookErrorCodes.BodyTooLarge);
            return;
        }

        // The configuration first: a slug nobody configured costs no query.
        var secrets = settings.SecretsOf(slug);
        if (secrets.Count == 0 || !await webhookOrders.IsActiveChannelAsync(slug, httpContext.RequestAborted))
        {
            Refuse(context, StatusCodes.Status404NotFound, WebhookErrorCodes.ChannelNotFound);
            return;
        }

        var timestamp = httpContext.Request.Headers[WebhookSignature.TimestampHeader].ToString();
        if (!WebhookSignature.TryParseTimestamp(timestamp, out var sentAt)
            || !WebhookSignature.IsWithinTolerance(sentAt, timeProvider.GetUtcNow(), settings.TimestampTolerance))
        {
            Refuse(context, StatusCodes.Status401Unauthorized, WebhookErrorCodes.TimestampInvalid);
            return;
        }

        var signatures = WebhookSignature.ParseSignatures(httpContext.Request.Headers[WebhookSignature.SignatureHeader].ToString());
        if (!WebhookSignature.Matches(secrets, timestamp, body, signatures))
        {
            Refuse(context, StatusCodes.Status401Unauthorized, WebhookErrorCodes.SignatureInvalid);
            return;
        }

        httpContext.User = WebhookCaller.PrincipalOf(slug);
        httpContext.Request.Body = new MemoryStream(body, writable: false);
        await next();
    }

    private void Refuse(ResourceExecutingContext context, int statusCode, string errorCode)
    {
        WebhookRejection.Log(logger, context.HttpContext, statusCode, errorCode);
        context.Result = WebhookRejection.Result(context.HttpContext, statusCode, errorCode);
    }

    /// <summary>The whole body, or null when it is longer than <paramref name="maxBytes"/> (it is not read to its end then).</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }
}
