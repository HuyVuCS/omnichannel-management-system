using System.Text.Json;
using System.Text.Json.Nodes;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Tests.Workers;

public sealed class MessageEnvelopeTests
{
    [Fact]
    public void Order_created_envelope_carries_the_task_id_and_payload_object()
    {
        var taskId = Guid.CreateVersion7();
        var row = new OutboxEvent(EventTypes.OrderCreated, taskId.ToString(), """{"taskId":"x","items":[1,2]}""", "corr-1", null);

        var envelope = MessageEnvelope.FromOutboxEvent(row);
        var json = JsonNode.Parse(MessageJson.Serialize(envelope))!.AsObject();

        Assert.Equal((row.OutboxEventId.ToString(), "corr-1", taskId, EventTypes.OrderCreated),
            (envelope.MessageId, envelope.CorrelationId, envelope.TaskId, envelope.EventType));
        Assert.Equal(row.CreatedAt, envelope.OccurredAt);
        Assert.Equal(JsonValueKind.Object, json["payload"]!.GetValueKind());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(row.Payload), json["payload"]));
        Assert.Equal(
            new[] { "correlationId", "eventType", "messageId", "occurredAt", "payload", "taskId" },
            json.Select(p => p.Key).Order());
    }

    [Fact]
    public void Inventory_changed_envelope_has_no_task_id()
    {
        var row = new OutboxEvent(EventTypes.InventoryChanged, Guid.NewGuid().ToString(), "{}", "corr-2", Guid.NewGuid());

        var json = JsonNode.Parse(MessageJson.Serialize(MessageEnvelope.FromOutboxEvent(row)))!;

        Assert.Null(json["taskId"]);
    }
}
