using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Orders;

public sealed record OrderChannelResponse(Guid ChannelId, string Slug, string Name);

public sealed record OrderCustomerResponse(Guid CustomerId, string Name);

/// <param name="ItemCount">Number of order lines.</param>
/// <param name="PaymentStatus">Independent of <paramref name="Status"/>: a paid order keeps its status.</param>
public sealed record OrderSummaryResponse(
    Guid OrderId,
    string ExternalOrderId,
    OrderStatus Status,
    decimal TotalAmount,
    int ItemCount,
    OrderChannelResponse Channel,
    OrderCustomerResponse Customer,
    DateTimeOffset CreatedAt,
    PaymentStatus PaymentStatus);

/// <summary>
/// An order with its lines, its payment, its invoice and every processing task (with all attempts) tied to it.
/// </summary>
/// <param name="Invoice">Null until the invoice task of the order has succeeded.</param>
public sealed record OrderDetailResponse(
    Guid OrderId,
    string ExternalOrderId,
    OrderStatus Status,
    decimal TotalAmount,
    int ItemCount,
    OrderChannelResponse Channel,
    OrderCustomerResponse Customer,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderItemResponse> Items,
    IReadOnlyList<OrderTaskResponse> Tasks,
    OrderPaymentResponse Payment,
    OrderInvoiceResponse? Invoice);

/// <summary>The payment of an order; every field but <paramref name="Status"/> is null while it is <c>Unpaid</c>.</summary>
/// <param name="PaidByEmail">The e-mail address of the user who recorded the payment; null when that user is gone.</param>
/// <param name="Reference">The optional reference entered with the payment.</param>
public sealed record OrderPaymentResponse(
    PaymentStatus Status,
    DateTimeOffset? PaidAt,
    string? PaidByEmail,
    PaymentMethod? Method,
    string? Reference);

/// <param name="Number">The number the invoice provider issued.</param>
public sealed record OrderInvoiceResponse(string Number, DateTimeOffset IssuedAt);

/// <param name="Stock">Ordered quantity.</param>
/// <param name="UnitPrice">Price snapshot taken at intake.</param>
public sealed record OrderItemResponse(
    Guid ProductId,
    string Sku,
    string ProductName,
    int Stock,
    decimal UnitPrice,
    decimal LineTotal);

/// <param name="ChannelId">The channel of a <c>StockSync</c> task; null for other task types.</param>
/// <param name="ProductId">The product of a <c>StockSync</c> task; null for other task types.</param>
/// <param name="Attempts">Ordered by <c>attemptNo</c>.</param>
public sealed record OrderTaskResponse(
    Guid TaskId,
    ProcessingTaskType TaskType,
    ProcessingTaskStatus Status,
    Guid? ChannelId,
    Guid? ProductId,
    int RetryCount,
    string CorrelationId,
    string? MessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<ProcessingAttemptResponse> Attempts);

/// <summary>
/// Search matches the external order id or the customer name. <see cref="From"/>/<see cref="To"/> filter on <c>CreatedAt</c>
/// (inclusive). Default order: newest first (<c>createdAt</c>, descending unless <c>sortDir=asc</c>).
/// </summary>
public sealed record OrderListQuery : PageQuery
{
    public const string SortByCreatedAt = "createdAt";
    public const string SortByTotalAmount = "totalAmount";
    public const string SortByExternalOrderId = "externalOrderId";

    public static readonly IReadOnlyList<string> SortFields = [SortByCreatedAt, SortByTotalAmount, SortByExternalOrderId];

    /// <summary>Channel slug (case-insensitive); an unknown slug matches nothing.</summary>
    public string? Channel { get; init; }

    public OrderStatus? Status { get; init; }
    public Guid? CustomerId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    public OrderFilter ToFilter() => new(Channel, Status, CustomerId, From, To, SearchTerm);
}

/// <summary>
/// Which orders: the filters of the order list, shared with the orders export so that a file contains what the list
/// shows for the same filters.
/// </summary>
/// <param name="Channel">Channel slug (case-insensitive); an unknown slug matches nothing.</param>
/// <param name="From">Inclusive, on <c>CreatedAt</c>.</param>
/// <param name="To">Inclusive, on <c>CreatedAt</c>.</param>
/// <param name="Search">Matches the external order id or the customer name; null or blank = no search.</param>
public sealed record OrderFilter(
    string? Channel,
    OrderStatus? Status,
    Guid? CustomerId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Search);
