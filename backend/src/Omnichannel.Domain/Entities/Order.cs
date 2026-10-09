using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

/// <summary>
/// Sales order. Idempotency key: UNIQUE(ChannelId, ExternalOrderId).
/// <para>
/// Payment is recorded next to the status, never in it: a paid order keeps its <see cref="Status"/>. An order is paid
/// once and in full; the four payment fields are all null while <see cref="PaymentStatus"/> is <c>Unpaid</c>.
/// </para>
/// </summary>
public class Order : AuditableEntity
{
    public const int MaxPaymentReferenceLength = 100;

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

    public PaymentStatus PaymentStatus { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>The user who recorded the payment (a user id, like the audit fields).</summary>
    public string? PaidBy { get; private set; }

    public PaymentMethod? PaymentMethod { get; private set; }

    /// <summary>Optional reference of the payment (a receipt or transfer number), as entered.</summary>
    public string? PaymentReference { get; private set; }

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

    /// <summary>
    /// Records the one payment of the order. Only an order whose stock is reserved can be paid, and only once. Neither
    /// <see cref="Status"/> nor any stock changes. A blank <paramref name="reference"/> is no reference.
    /// </summary>
    public void MarkPaid(PaymentMethod method, string? reference, string paidBy, DateTimeOffset paidAt)
    {
        if (Status != OrderStatus.Reserved)
        {
            throw new InvalidOperationException($"Order {OrderId} is {Status}; only a Reserved order can be paid.");
        }

        if (PaymentStatus == PaymentStatus.Paid)
        {
            throw new InvalidOperationException($"Order {OrderId} is already paid.");
        }

        if (!Enum.IsDefined(method)) throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown payment method.");
        ArgumentException.ThrowIfNullOrWhiteSpace(paidBy);

        var trimmed = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        if (trimmed is { Length: > MaxPaymentReferenceLength })
        {
            throw new ArgumentException($"A payment reference has at most {MaxPaymentReferenceLength} characters.", nameof(reference));
        }

        PaymentStatus = PaymentStatus.Paid;
        PaidAt = paidAt;
        PaidBy = paidBy;
        PaymentMethod = method;
        PaymentReference = trimmed;
    }
}
