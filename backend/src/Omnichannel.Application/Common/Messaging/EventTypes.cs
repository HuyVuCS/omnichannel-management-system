namespace Omnichannel.Application.Common.Messaging;

/// <summary>
/// Event types stored in <c>OutboxEvent.EventType</c>; they double as RabbitMQ routing keys on the
/// <c>omnichannel.topic</c> exchange, unless the row names its own routing key (<see cref="StockSyncRequested"/>).
/// </summary>
public static class EventTypes
{
    public const string OrderCreated = "order.created";
    public const string InventoryChanged = "inventory.changed";
    public const string InvoiceRequested = "invoice.requested";
    public const string EmailRequested = "email.requested";
    public const string ProcessingRetry = "processing.retry";

    /// <summary>
    /// Directed at one channel: push the product's current stock (initial sync of a mapping, manual retry of a stock-sync
    /// task). Published with the channel's stock-sync queue name as the routing key, never with the event type.
    /// </summary>
    public const string StockSyncRequested = "stock.sync.requested";

    /// <summary>Build the Excel file of an export task (queue <c>report.export</c>).</summary>
    public const string ReportExportRequested = "report.export.requested";
}
