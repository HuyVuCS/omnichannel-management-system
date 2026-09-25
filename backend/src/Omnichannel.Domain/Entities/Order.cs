using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

/// <summary>
/// Sales order. Idempotency key: UNIQUE(ChannelId, ExternalOrderId).
/// </summary>
public class Order : AuditableEntity
{
    private readonly List<OrderItem> _items = [];

    private Order()
    {
    }

    public Order(Guid channelId, Guid customerId, string externalOrderId, OrderStatus status)
    {
        if (channelId == Guid.Empty) throw new ArgumentException("ChannelId is required.", nameof(channelId));
        if (customerId == Guid.Empty) throw new ArgumentException("CustomerId is required.", nameof(customerId));
        ArgumentException.ThrowIfNullOrWhiteSpace(externalOrderId);

        OrderId = Guid.CreateVersion7();
        ChannelId = channelId;
        CustomerId = customerId;
        ExternalOrderId = externalOrderId.Trim();
        Status = status;
        TotalAmount = 0m;
    }

    public Guid OrderId { get; private set; }
    public Guid ChannelId { get; private set; }
    public Guid CustomerId { get; private set; }
    public string ExternalOrderId { get; private set; } = null!;
    public OrderStatus Status { get; private set; }
    public decimal TotalAmount { get; private set; }

    public Channel Channel { get; private set; } = null!;
    public Customer Customer { get; private set; } = null!;
    public IReadOnlyCollection<OrderItem> Items => _items;

    public OrderItem AddItem(Guid productId, int stock, decimal unitPrice)
    {
        if (_items.Any(i => i.ProductId == productId))
        {
            throw new InvalidOperationException($"Product {productId} is already on order {OrderId}.");
        }

        var item = new OrderItem(OrderId, productId, stock, unitPrice);
        _items.Add(item);
        TotalAmount += item.Stock * item.UnitPrice;
        return item;
    }
}
