namespace Omnichannel.Application.Invoices;

/// <summary>
/// Issues the invoice of a paid order at the invoice provider (doc §8.4). One implementation per provider; the Invoice
/// Worker only knows this interface, so a real e-invoice provider replaces the simulated one without touching it.
/// </summary>
public interface IInvoiceProviderClient
{
    /// <summary>
    /// Issues the invoice of <paramref name="request"/>. <see cref="InvoiceRequest.Key"/> is the idempotency key: a
    /// provider that is asked twice with one key returns the invoice it issued the first time.
    /// </summary>
    /// <exception cref="InvoiceProviderException">The provider did not issue the invoice.</exception>
    Task<IssuedInvoice> IssueAsync(InvoiceRequest request, CancellationToken cancellationToken);
}

/// <param name="Key">Idempotency key of the invoice: <c>invoice:{orderId}</c>.</param>
/// <param name="Total">The order's total.</param>
/// <param name="AttemptNo">The attempt of the invoice task that makes the call (for the provider's own diagnostics).</param>
public sealed record InvoiceRequest(
    string Key,
    Guid OrderId,
    string ExternalOrderId,
    string CustomerName,
    IReadOnlyList<InvoiceLine> Lines,
    decimal Total,
    DateTimeOffset PaidAt,
    int AttemptNo);

/// <param name="UnitPrice">The price snapshot of the order line.</param>
public sealed record InvoiceLine(string Sku, string Name, int Quantity, decimal UnitPrice);

/// <param name="InvoiceNumber">The provider's number of the invoice.</param>
public sealed record IssuedInvoice(string InvoiceNumber, DateTimeOffset IssuedAt);

/// <summary>
/// A call to the invoice provider failed. <see cref="Exception.Message"/> is sanitised (safe for the attempt history
/// and the dashboard: no credentials, no response bodies).
/// </summary>
/// <param name="errorCode">One of the provider codes of <see cref="InvoiceErrorCodes"/>.</param>
/// <param name="isTransient">True when trying again later can succeed (retry with delay); false = permanent failure.</param>
public sealed class InvoiceProviderException(string errorCode, string message, bool isTransient, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;

    public bool IsTransient { get; } = isTransient;
}

/// <summary><c>ErrorCode</c> values of a failed invoice attempt.</summary>
public static class InvoiceErrorCodes
{
    /// <summary>No answer within the call timeout (transient).</summary>
    public const string ProviderTimeout = "invoice_provider_timeout";

    /// <summary>The provider is down or answered 5xx (transient).</summary>
    public const string ProviderUnavailable = "invoice_provider_unavailable";

    /// <summary>The provider refused the invoice (permanent).</summary>
    public const string ProviderRejected = "invoice_provider_rejected";

    /// <summary>Not called: the provider's circuit breaker is open after repeated transient failures (transient).</summary>
    public const string ProviderCircuitOpen = "invoice_provider_circuit_open";

    /// <summary>The order of the task is not paid: there is nothing to invoice (permanent).</summary>
    public const string OrderNotPaid = "invoice_order_not_paid";
}

/// <summary>SystemEventLog event types written by the Invoice Worker.</summary>
public static class InvoiceEvents
{
    /// <summary>A message of <c>invoice.processing</c> was sent to the DLQ without processing (Error).</summary>
    public const string MessageRejected = "invoice.message_rejected";
}
