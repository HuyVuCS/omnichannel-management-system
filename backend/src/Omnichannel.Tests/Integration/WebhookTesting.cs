using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// A sender of webhooks for the tests of task 023. It signs on its own - HMAC-SHA256 over <c>"{timestamp}.{body}"</c>,
/// written here from the contract, not with the API's code - so the tests would notice a change of the scheme.
/// </summary>
internal static class WebhookTesting
{
    public const string TimestampHeader = "X-Webhook-Timestamp";
    public const string SignatureHeader = "X-Webhook-Signature";

    public static string Now(int offsetSeconds = 0) => (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + offsetSeconds).ToString();

    /// <summary>The hex digits of the signature (lower case), without the <c>v1=</c> in front.</summary>
    public static string SignatureOf(string secret, string timestamp, byte[] body)
    {
        var message = Encoding.ASCII.GetBytes(timestamp + ".").Concat(body).ToArray();
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    /// <summary>
    /// <c>POST /api/webhooks/{slug}</c> with <paramref name="body"/> exactly as given. Signed with <paramref name="secret"/>
    /// for <paramref name="timestamp"/> (now when null) unless <paramref name="signature"/> gives the header itself; a
    /// null secret and no signature leave the header out, <paramref name="sendTimestamp"/> false the timestamp.
    /// </summary>
    public static HttpRequestMessage Request(
        string slug,
        byte[] body,
        string? secret,
        string? timestamp = null,
        string? signature = null,
        bool sendTimestamp = true,
        string? language = null,
        string? correlationId = null,
        HttpContent? content = null)
    {
        timestamp ??= Now();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{slug}")
        {
            Content = content ?? new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (sendTimestamp)
        {
            request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
        }

        signature ??= secret is null ? null : "v1=" + SignatureOf(secret, timestamp, body);
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation(SignatureHeader, signature);
        }

        if (language is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", language);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlationId);
        }

        return request;
    }

    /// <summary>A webhook order as a channel would write it (compact JSON, camelCase).</summary>
    public static byte[] Order(string externalOrderId, object customer, params object[] items) =>
        JsonSerializer.SerializeToUtf8Bytes(new { externalOrderId, customer, items });

    public static object Customer(string externalCustomerId, string name = "Webhook Customer", string? phone = null, string? email = null) =>
        new { externalCustomerId, name, phone, email };

    public static object ById(string externalProductId, int quantity) => new { externalProductId, quantity };

    public static object BySku(string externalSku, int quantity) => new { externalSku, quantity };

    public static async Task<Dictionary<string, string[]>> ErrorsAsync(this HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", document.RootElement.GetProperty("errorCode").GetString());
        return document.RootElement.GetProperty("errors").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToArray());
    }

    /// <summary>A body whose length the client does not announce (no <c>Content-Length</c>: it is sent in chunks).</summary>
    public sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
