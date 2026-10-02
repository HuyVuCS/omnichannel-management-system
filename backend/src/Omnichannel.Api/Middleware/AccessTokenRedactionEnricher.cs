using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Omnichannel.Api.Middleware;

/// <summary>
/// The realtime hub accepts the access token in the query string (browsers cannot set headers on WebSocket requests).
/// A query string must therefore never reach a log with the token in it - whatever level somebody configures for the
/// framework categories that log request URLs (<c>Microsoft.AspNetCore.Hosting.Diagnostics</c> does at Information).
/// This enricher replaces the value of every <c>access_token=</c> it finds in a text property of a log event by
/// <c>***</c>; rendered messages are built from the properties, so they are covered too.
/// </summary>
public sealed partial class AccessTokenRedactionEnricher : ILogEventEnricher
{
    private const string Marker = "access_token";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        List<LogEventProperty>? redacted = null;
        foreach (var (name, value) in logEvent.Properties)
        {
            if (value is ScalarValue { Value: string text } && text.Contains(Marker, StringComparison.OrdinalIgnoreCase))
            {
                (redacted ??= []).Add(new LogEventProperty(name, new ScalarValue(Redact(text))));
            }
        }

        if (redacted is not null)
        {
            foreach (var property in redacted)
            {
                logEvent.AddOrUpdateProperty(property);
            }
        }
    }

    public static string Redact(string text) => TokenPattern().Replace(text, "$1***");

    [GeneratedRegex(@"(access_token=)[^&\s#""']+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
}
