using Microsoft.Net.Http.Headers;

namespace Omnichannel.Api.Middleware;

/// <summary>
/// The headers every answer of this API carries, whatever produced it (an action, the exception handler, the rate
/// limiter, a 404, the hub): the API serves JSON and ProblemDetails only, so nothing it answers may be sniffed into
/// another type, rendered as a page, framed or kept by a cache.
/// <list type="bullet">
/// <item><c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy: no-referrer</c>;</item>
/// <item><c>Content-Security-Policy: default-src 'none'; frame-ancestors 'none'</c> - except on the OpenAPI document and
/// the Swagger UI of Development, which is a page with scripts;</item>
/// <item><c>Cache-Control: no-store</c> unless the answer set its own: access tokens (login), presigned links and
/// business data must not be stored by a browser or a proxy.</item>
/// </list>
/// No <c>Cross-Origin-Resource-Policy</c>: the frontend is on another site than the API and reads these answers.
/// HSTS is not set here (<c>UseHsts</c>, HTTPS only).
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public const string ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    private static readonly PathString[] DocumentationPaths = ["/swagger", "/openapi"];

    private readonly bool _servesDocumentation = environment.IsDevelopment();

    public Task InvokeAsync(HttpContext context)
    {
        // When the answer starts, not now: the exception handler clears the headers of an answer it replaces.
        context.Response.OnStarting(Apply, context);
        return next(context);
    }

    private Task Apply(object state)
    {
        var context = (HttpContext)state;
        var headers = context.Response.Headers;

        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        if (!(_servesDocumentation && DocumentationPaths.Any(path => context.Request.Path.StartsWithSegments(path))))
        {
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
        }

        if (!headers.ContainsKey(HeaderNames.CacheControl))
        {
            headers.CacheControl = "no-store";
        }

        return Task.CompletedTask;
    }
}
