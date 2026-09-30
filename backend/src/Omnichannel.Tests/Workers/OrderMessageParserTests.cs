using System.Text;
using System.Text.Json;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Workers.OrderWorker;

namespace Omnichannel.Tests.Workers;

public sealed class OrderMessageParserTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();

    [Fact]
    public void Parses_a_valid_order_created_message()
    {
        var parsed = OrderMessageParser.TryParse(Envelope(Payload()), out var message, out var reason);

        Assert.True(parsed);
        Assert.Null(reason);
        Assert.Equal(TaskId, message!.TaskId);
        Assert.Equal("EXT-1", message.Order.ExternalOrderId);
        Assert.Equal(2, message.Order.Items.Count);
        Assert.Equal("corr-1", message.Envelope.CorrelationId);
    }

    public static TheoryData<string, byte[]> PoisonMessages() => new()
    {
        { "invalid JSON", Encoding.UTF8.GetBytes("{ nope") },
        { "null", Encoding.UTF8.GetBytes("null") },
        { "wrong event type", Envelope(Payload(), eventType: EventTypes.InventoryChanged) },
        { "missing task id", Envelope(Payload(), withoutTaskId: true) },
        { "payload is not an object", Envelope("[1,2]") },
        { "payload task id differs", Envelope(Payload(taskId: Guid.NewGuid())) },
        { "no items", Envelope(Payload(items: [])) },
        { "zero stock", Envelope(Payload(items: [new OrderCreatedItem(Guid.NewGuid(), 0, 1m)])) },
        { "duplicate product", Envelope(Payload(items: [new OrderCreatedItem(DuplicateProduct, 1, 1m), new OrderCreatedItem(DuplicateProduct, 2, 1m)])) },
        { "missing customer", Envelope(Payload(customerId: Guid.Empty)) }
    };

    private static readonly Guid DuplicateProduct = Guid.NewGuid();

    [Theory]
    [MemberData(nameof(PoisonMessages))]
    public void Classifies_unprocessable_messages_as_poison(string _, byte[] body)
    {
        var parsed = OrderMessageParser.TryParse(body, out var message, out var reason);

        Assert.False(parsed);
        Assert.Null(message);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    private static string Payload(Guid? taskId = null, Guid? customerId = null, OrderCreatedItem[]? items = null) =>
        MessageJson.Serialize(new OrderCreatedPayload(
            taskId ?? TaskId,
            Guid.NewGuid(),
            "website",
            "EXT-1",
            customerId ?? Guid.NewGuid(),
            items ?? [new OrderCreatedItem(Guid.NewGuid(), 1, 9.99m), new OrderCreatedItem(Guid.NewGuid(), 3, 0m)],
            DateTimeOffset.UtcNow,
            "user-1"));

    private static byte[] Envelope(string payloadJson, string eventType = EventTypes.OrderCreated, bool withoutTaskId = false)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        var envelope = new MessageEnvelope(
            Guid.NewGuid().ToString(),
            "corr-1",
            withoutTaskId ? null : TaskId,
            eventType,
            DateTimeOffset.UtcNow,
            payload.RootElement.Clone());
        return MessageJson.SerializeToUtf8Bytes(envelope);
    }
}
