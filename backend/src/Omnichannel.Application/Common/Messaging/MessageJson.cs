using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omnichannel.Application.Common.Messaging;

/// <summary>The one JSON format for every outbox payload / message body: camelCase, enums as strings.</summary>
public static class MessageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"Message payload is empty; expected {typeof(T).Name}.");
}
