using Microsoft.Extensions.Options;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Invoices;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Workers.Processing;

namespace Omnichannel.Workers.InvoiceWorker;

/// <summary>
/// Handles one <c>invoice.requested</c> delivery (doc §8.4): issues the invoice of a paid order at the provider and
/// stores it. The delivery, attempt and failure handling is <see cref="TaskRequestMessageHandler"/>'s; the work is:
/// <list type="bullet">
/// <item>the order with its lines and customer is read without a lock;</item>
/// <item>an order whose invoice is stored already (a redelivery after the commit of an earlier one) completes the task
/// <b>without a call</b>;</item>
/// <item>an order that is not paid fails for good (<c>invoice_order_not_paid</c>);</item>
/// <item>the provider is called outside any transaction, with the key <c>invoice:{orderId}</c>: asked twice (a redelivery
/// after the call and before the commit), it answers with the same invoice;</item>
/// <item>one transaction stores the invoice and completes attempt and task.</item>
/// </list>
/// Nothing here looks at the e-mail task of the order, and the order itself is never changed.
/// </summary>
public sealed class InvoiceMessageHandler(
    IInvoiceProcessingService invoices,
    IInvoiceProviderClient provider,
    WorkerCurrentUser currentUser,
    WorkerCorrelationContext correlationContext,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    ILogger<InvoiceMessageHandler> logger)
    : TaskRequestMessageHandler(Worker, invoices, currentUser, correlationContext, workerOptions, lifetime, logger)
{
    public const string WorkerName = "InvoiceWorker";
    public const string Actor = "system:invoice-worker";

    private static readonly TaskRequestWorker Worker = new(WorkerName, Actor, EventTypes.InvoiceRequested, ProcessingTaskType.Invoice);

    private readonly IInvoiceProcessingService _invoices = invoices;
    private readonly IInvoiceProviderClient _provider = provider;
    private readonly ILogger _logger = logger;

    protected override async Task<PermanentFailure?> ProcessAsync(
        Guid taskId,
        Guid orderId,
        AttemptStart attempt,
        CancellationToken stopping,
        CancellationToken cancellationToken)
    {
        var order = await _invoices.ReadOrderAsync(orderId, cancellationToken);

        if (order.HasInvoice)
        {
            await _invoices.CompleteAsync(taskId, attempt.AttemptId, invoice: null, cancellationToken);
            _logger.LogInformation(
                "The invoice of the order is stored already: task completed without a call to the provider (attempt {AttemptNo})",
                attempt.AttemptNo);
            return null;
        }

        if (order.PaymentStatus != PaymentStatus.Paid || order.PaidAt is not { } paidAt)
        {
            return new PermanentFailure(InvoiceErrorCodes.OrderNotPaid, "The order of the invoice task is not paid.");
        }

        var key = Invoice.ProviderKeyOf(order.OrderId);
        var issued = await _provider.IssueAsync(
            new InvoiceRequest(
                key,
                order.OrderId,
                order.ExternalOrderId,
                order.CustomerName,
                order.Lines,
                order.TotalAmount,
                paidAt,
                attempt.AttemptNo),
            stopping);

        // The total is a snapshot: what the provider invoiced, whatever happens to the order later.
        await _invoices.CompleteAsync(
            taskId,
            attempt.AttemptId,
            new Invoice(order.OrderId, issued.InvoiceNumber, issued.IssuedAt, order.TotalAmount, key),
            cancellationToken);

        _logger.LogInformation("Invoice {InvoiceNumber} issued and stored (attempt {AttemptNo})", issued.InvoiceNumber, attempt.AttemptNo);
        return null;
    }

    protected override bool IsProviderAnswer(Exception exception) => exception is InvoiceProviderException;
}
