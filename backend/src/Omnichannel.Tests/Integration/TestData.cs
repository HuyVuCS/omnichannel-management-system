using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omnichannel.Tests.Integration;

/// <summary>Tests share one database and never clean it: every test uses its own unique values.</summary>
internal static class TestData
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string UniqueSuffix() => Guid.NewGuid().ToString("N")[..12];

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    /// <summary>The <c>errorCode</c> extension of a ProblemDetails body.</summary>
    public static async Task<string?> ReadErrorCodeAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errorCode", out var errorCode) ? errorCode.GetString() : null;
    }
}
