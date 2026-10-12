using Serilog.Events;

namespace Omnichannel.Api.Middleware;

/// <summary>
/// The properties of the request log line ("HTTP GET /api/orders responded 200 in 12.3 ms"). The path is written as it
/// travels in a URL (percent-encoded), not decoded: the text log of Development prints the message as it is, and a
/// decoded <c>%0A</c> would start a line of the attacker's choice there. (The compact JSON log of every other
/// environment escapes control characters by itself; it gets the same value.)
/// </summary>
public static class RequestLogProperties
{
    public static IEnumerable<LogEventProperty> Create(HttpContext httpContext, string requestPath, double elapsedMs, int statusCode) =>
    [
        new LogEventProperty("RequestMethod", new ScalarValue(httpContext.Request.Method)),
        new LogEventProperty("RequestPath", new ScalarValue(EncodedPath(httpContext.Request))),
        new LogEventProperty("StatusCode", new ScalarValue(statusCode)),
        new LogEventProperty("Elapsed", new ScalarValue(elapsedMs))
    ];

    public static string EncodedPath(HttpRequest request) => request.PathBase.Add(request.Path).ToUriComponent();
}
