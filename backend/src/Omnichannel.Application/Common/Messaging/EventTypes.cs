namespace Omnichannel.Application.Common.Messaging;

/// <summary>
/// Event types stored in <c>OutboxEvent.EventType</c>; they double as RabbitMQ routing keys on the
/// <c>omnichannel.topic</c> exchange.
/// </summary>
public static class EventTypes
{
    public const string OrderCreated = "order.created";
    public const string InventoryChanged = "inventory.changed";
    public const string InvoiceRequested = "invoice.requested";
    public const string EmailRequested = "email.requested";
    public const string ProcessingRetry = "processing.retry";
}
