namespace Omnichannel.Domain.Entities;

/// <summary>
/// The invoice the provider issued for a paid order. 1:1 with its <see cref="Order"/> (<see cref="OrderId"/> is both
/// primary and foreign key): an order has at most one invoice, whatever the number of deliveries or retries of its
/// invoice task. The task holds status and attempts; this row holds the result.
/// </summary>
public class Invoice : AuditableEntity
{
    public const int MaxInvoiceNumberLength = 64;
    public const int MaxProviderReferenceLength = 128;

    private Invoice()
    {
    }

    /// <param name="totalAmount">The order's total when the invoice was issued (a snapshot, not a reference).</param>
    /// <param name="providerReference">The idempotency key the provider was called with.</param>
    public Invoice(Guid orderId, string invoiceNumber, DateTimeOffset issuedAt, decimal totalAmount, string providerReference)
    {
        if (orderId == Guid.Empty) throw new ArgumentException("An invoice belongs to an order.", nameof(orderId));
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(invoiceNumber.Length, MaxInvoiceNumberLength, nameof(invoiceNumber));
        ArgumentOutOfRangeException.ThrowIfNegative(totalAmount);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerReference);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(providerReference.Length, MaxProviderReferenceLength, nameof(providerReference));

        OrderId = orderId;
        InvoiceNumber = invoiceNumber;
        IssuedAt = issuedAt;
        TotalAmount = totalAmount;
        ProviderReference = providerReference;
    }

    public Guid OrderId { get; private set; }

    /// <summary>The number the provider gave the invoice.</summary>
    public string InvoiceNumber { get; private set; } = null!;

    public DateTimeOffset IssuedAt { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string ProviderReference { get; private set; } = null!;

    public Order Order { get; private set; } = null!;

    /// <summary>
    /// The key an order's invoice is requested with. It depends on the order alone, so a provider that is asked twice
    /// (a redelivery, a retry) issues one invoice.
    /// </summary>
    public static string ProviderKeyOf(Guid orderId) => $"invoice:{orderId}";
}
