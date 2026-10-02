using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Domain;

/// <summary>Task 014: domain rules of channel product mappings, stock-sync tasks and directed outbox rows.</summary>
public sealed class ChannelProductTests
{
    private static readonly Guid ChannelId = Guid.NewGuid();
    private static readonly Guid ProductId = Guid.NewGuid();

    [Fact]
    public void New_mapping_is_active_with_trimmed_ids_and_blank_becomes_null()
    {
        var mapping = new ChannelProduct(ChannelId, ProductId, "  EXT-1 ", "   ");

        Assert.Equal((ChannelProductStatus.Active, "EXT-1", (string?)null), (mapping.Status, mapping.ExternalProductId, mapping.ExternalSku));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", " ")]
    public void A_mapping_needs_an_external_product_id_or_an_external_sku(string? externalProductId, string? externalSku)
    {
        Assert.Throws<ArgumentException>(() => new ChannelProduct(ChannelId, ProductId, externalProductId, externalSku));

        var mapping = new ChannelProduct(ChannelId, ProductId, "EXT-1", null);
        Assert.Throws<ArgumentException>(() => mapping.Update(externalProductId, externalSku, ChannelProductStatus.Active));
        Assert.Equal("EXT-1", mapping.ExternalProductId);
    }

    [Fact]
    public void External_ids_are_limited_to_128_characters_after_trimming()
    {
        var longest = new string('x', ChannelProduct.MaxExternalIdLength);

        var mapping = new ChannelProduct(ChannelId, ProductId, $" {longest} ", longest);
        Assert.Equal((longest, longest), (mapping.ExternalProductId, mapping.ExternalSku));

        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelProduct(ChannelId, ProductId, longest + "x", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Update(null, longest + "x", ChannelProductStatus.Active));
    }

    [Fact]
    public void Channel_and_product_are_required()
    {
        Assert.Throws<ArgumentException>(() => new ChannelProduct(Guid.Empty, ProductId, "EXT-1", null));
        Assert.Throws<ArgumentException>(() => new ChannelProduct(ChannelId, Guid.Empty, "EXT-1", null));
    }

    [Fact]
    public void Update_asks_for_a_sync_only_when_the_channel_must_learn_the_stock_again()
    {
        var mapping = new ChannelProduct(ChannelId, ProductId, "EXT-1", "SKU-1");

        // Nothing changed (also not when the same values arrive with spaces).
        Assert.False(mapping.Update(" EXT-1 ", "SKU-1", ChannelProductStatus.Active));
        // An external id changed while Active.
        Assert.True(mapping.Update("EXT-2", "SKU-1", ChannelProductStatus.Active));
        Assert.True(mapping.Update("EXT-2", null, ChannelProductStatus.Active));
        // Deactivating never syncs (no zero push), nor does a change while Inactive.
        Assert.False(mapping.Update("EXT-2", null, ChannelProductStatus.Inactive));
        Assert.False(mapping.Update("EXT-3", "SKU-3", ChannelProductStatus.Inactive));
        // Re-activation syncs, with or without other changes.
        Assert.True(mapping.Update("EXT-3", "SKU-3", ChannelProductStatus.Active));

        Assert.Equal(("EXT-3", "SKU-3", ChannelProductStatus.Active), (mapping.ExternalProductId, mapping.ExternalSku, mapping.Status));
        Assert.Equal((ChannelId, ProductId), (mapping.ChannelId, mapping.ProductId));
    }

    [Fact]
    public void Update_rejects_an_unknown_status()
    {
        var mapping = new ChannelProduct(ChannelId, ProductId, "EXT-1", null);

        Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Update("EXT-1", null, (ChannelProductStatus)99));
    }
}

public sealed class StockSyncTaskTests
{
    [Fact]
    public void Stock_sync_task_carries_channel_and_product_and_no_order()
    {
        var (channelId, productId) = (Guid.NewGuid(), Guid.NewGuid());

        var task = ProcessingTask.ForStockSync(channelId, productId, "corr-1", "msg-1", "stocksync:key");

        Assert.Equal((ProcessingTaskType.StockSync, ProcessingTaskStatus.Pending, (Guid?)channelId, (Guid?)productId, (Guid?)null),
            (task.TaskType, task.Status, task.ChannelId, task.ProductId, task.OrderId));
        Assert.Equal(("corr-1", "msg-1", "stocksync:key"), (task.CorrelationId, task.MessageId, task.IdempotencyKey));
    }

    [Fact]
    public void Stock_sync_task_requires_channel_and_product()
    {
        Assert.Throws<ArgumentException>(() => ProcessingTask.ForStockSync(Guid.Empty, Guid.NewGuid(), "corr", null));
        Assert.Throws<ArgumentException>(() => ProcessingTask.ForStockSync(Guid.NewGuid(), Guid.Empty, "corr", null));
    }

    [Fact]
    public void Tasks_of_other_types_have_no_channel_or_product()
    {
        var task = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, "corr", "msg");

        Assert.Equal(((Guid?)null, (Guid?)null), (task.ChannelId, task.ProductId));
    }

    [Fact]
    public void Idempotency_key_names_channel_and_message_and_fits_the_column()
    {
        var channelId = new Guid("0199a1b0-0000-7000-8000-000000000002");

        Assert.Equal("stocksync:0199a1b0000070008000000000000002:msg-1", StockSyncIdempotencyKey.For(channelId, "msg-1"));
        // Message ids are at most 100 characters (the parser rejects longer ones).
        Assert.True(StockSyncIdempotencyKey.For(channelId, new string('m', 100)).Length <= ProcessingTask.MaxIdempotencyKeyLength);
    }
}

public sealed class DirectedOutboxEventTests
{
    [Fact]
    public void Routing_key_defaults_to_the_event_type()
    {
        var row = new OutboxEvent(EventTypes.InventoryChanged, "p-1", "{}", "corr", null);

        Assert.Null(row.RoutingKey);
        Assert.Equal(EventTypes.InventoryChanged, row.EffectiveRoutingKey);
    }

    [Fact]
    public void Directed_row_is_routed_by_its_own_key()
    {
        var row = new OutboxEvent(EventTypes.StockSyncRequested, "t-1", "{}", "corr", null, routingKey: "stock.sync.pos");

        Assert.Equal(("stock.sync.pos", "stock.sync.pos", EventTypes.StockSyncRequested), (row.RoutingKey, row.EffectiveRoutingKey, row.EventType));
    }

    [Fact]
    public void Routing_key_must_not_be_blank_or_longer_than_an_amqp_short_string()
    {
        Assert.Throws<ArgumentException>(() => new OutboxEvent("e", "a", "{}", "corr", null, routingKey: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OutboxEvent("e", "a", "{}", "corr", null, routingKey: new string('k', OutboxEvent.MaxRoutingKeyLength + 1)));
    }

    [Fact]
    public void Stock_sync_requested_envelope_carries_the_task_id()
    {
        var taskId = Guid.CreateVersion7();
        var payload = MessageJson.Serialize(new StockSyncRequestedPayload(taskId, Guid.NewGuid(), Guid.NewGuid()));
        var row = new OutboxEvent(EventTypes.StockSyncRequested, taskId.ToString(), payload, "corr", null, routingKey: "stock.sync.website");

        var envelope = MessageEnvelope.FromOutboxEvent(row);

        Assert.Equal((taskId, EventTypes.StockSyncRequested), (envelope.TaskId, envelope.EventType));
        Assert.Equal(taskId, MessageJson.Deserialize<StockSyncRequestedPayload>(envelope.Payload.GetRawText()).TaskId);
    }
}
