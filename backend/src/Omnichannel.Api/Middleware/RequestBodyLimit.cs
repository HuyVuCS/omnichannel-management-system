using Microsoft.AspNetCore.Mvc;

namespace Omnichannel.Api.Middleware;

/// <summary>
/// The largest request body this API reads. The largest body it accepts by its own rules is a webhook
/// (<c>Webhooks:MaxBodyBytes</c>, 64 KiB); every JSON body of the dashboard is smaller (a stock import of 200 lines is
/// about 20 KiB, an order of 100 lines about 10 KiB - measured by a test). Twice the largest: 128 KiB. Images and
/// export files never pass through the API (presigned links to the storage).
/// <para>
/// The same number is Kestrel's <c>MaxRequestBodySize</c> (a body without a Content-Length is cut off there) and the
/// <c>request_body max_size</c> of the reverse proxy's API site (<c>docker/proxy/Caddyfile</c>; a test compares them).
/// </para>
/// </summary>
public static class RequestBodyLimit
{
    public const long MaxBytes = 128 * 1024;

    /// <summary><c>errorCode</c> of the 413 answer.</summary>
    public const string ErrorCode = "request_body_too_large";

    public static ProblemDetails Problem()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status413PayloadTooLarge,
            Title = "Request body too large",
            Detail = $"The body of the request is larger than this API accepts ({MaxBytes / 1024} KiB)."
        };
        problem.Extensions["errorCode"] = ErrorCode;
        return problem;
    }
}

/// <summary>
/// Answers a request whose announced body is longer than <see cref="RequestBodyLimit.MaxBytes"/> with 413 ProblemDetails
/// before anything of it is read. A body that announces no length is limited by Kestrel while it is read; that failure
/// becomes the same answer in <see cref="GlobalExceptionHandler"/>.
/// </summary>
public sealed class RequestBodyLimitMiddleware(
    RequestDelegate next,
    IProblemDetailsService problemDetailsService,
    ILogger<RequestBodyLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.ContentLength > RequestBodyLimit.MaxBytes)
        {
            logger.LogInformation(
                "Request refused with 413: announced body of {ContentLength} bytes, limit {MaxBytes}",
                context.Request.ContentLength,
                RequestBodyLimit.MaxBytes);
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = RequestBodyLimit.Problem()
            });
            return;
        }

        await next(context);
    }
}
