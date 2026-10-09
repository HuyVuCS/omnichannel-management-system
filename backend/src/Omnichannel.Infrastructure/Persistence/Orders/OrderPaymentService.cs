using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Persistence.Orders;

/// <summary>
/// The payment step (doc §8.4). Everything a payment writes is one transaction under the lock of the order row: two
/// requests for one order are serialised, and the second finds the order paid. Nothing is published here - the two
/// messages are outbox rows - and neither the order's status nor any stock is touched.
/// </summary>
internal sealed class OrderPaymentService(
    OmnichannelDbContext dbContext,
    ICurrentUser currentUser,
    ICorrelationContext correlationContext,
    TimeProvider timeProvider,
    ILogger<OrderPaymentService> logger) : IOrderPaymentService
{
    public async Task<PayOrderResponse> PayAsync(Guid orderId, PayOrderRequest request, CancellationToken cancellationToken)
    {
        var paidBy = currentUser.UserId ?? throw new InvalidOperationException("A payment needs an authenticated user.");
        var method = request.PaymentMethod ?? throw new InvalidOperationException("A payment needs a method (the validator checks it).");
        var now = timeProvider.GetUtcNow();
        var correlationId = correlationContext.CorrelationId;

        // No EnableRetryOnFailure: a retried transaction would need the execution strategy around it.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Row lock: of two payments of one order the second waits here and then sees the first.
        await dbContext.Database.ExecuteSqlAsync(
            $"""SELECT 1 FROM "Orders" WHERE "OrderId" = {orderId} FOR UPDATE""",
            cancellationToken);

        var order = await dbContext.Orders.SingleOrDefaultAsync(o => o.OrderId == orderId, cancellationToken)
            ?? throw new NotFoundException(nameof(Order), orderId, OrderErrorCodes.OrderNotFound);

        if (order.PaymentStatus == PaymentStatus.Paid)
        {
            throw new ConflictException(OrderErrorCodes.OrderAlreadyPaid, ValidationMessages.Get(ValidationMessages.Keys.OrderAlreadyPaid));
        }

        if (order.Status != OrderStatus.Reserved)
        {
            throw new ConflictException(OrderErrorCodes.OrderNotPayable, ValidationMessages.Get(ValidationMessages.Keys.OrderNotPayable));
        }

        // Read without a lock: the address decides only whether an e-mail task exists; the Notification Worker reads
        // the address again when it sends.
        var customerEmail = await dbContext.Customers
            .AsNoTracking()
            .Where(c => c.CustomerId == order.CustomerId)
            .Select(c => c.Email)
            .SingleAsync(cancellationToken);

        order.MarkPaid(method, request.PaymentReference, paidBy, now);

        var invoiceTask = AddTask(
            ProcessingTaskType.Invoice,
            order.OrderId,
            PaymentTaskKeys.ForInvoice(order.OrderId),
            EventTypes.InvoiceRequested,
            taskId => MessageJson.Serialize(new InvoiceRequestedPayload(taskId)),
            correlationId);

        // A customer without an address gets no e-mail task: a task that can never succeed should not exist.
        var emailTask = string.IsNullOrWhiteSpace(customerEmail)
            ? null
            : AddTask(
                ProcessingTaskType.Email,
                order.OrderId,
                PaymentTaskKeys.ForEmail(order.OrderId),
                EventTypes.EmailRequested,
                taskId => MessageJson.Serialize(new EmailRequestedPayload(taskId)),
                correlationId);

        // Audit: a financial action. The reference is stored with the order and stays out of the event.
        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Information,
            OrderPaymentEvents.Paid,
            correlationId,
            taskId: null,
            order.OrderId,
            workerName: null,
            MessageJson.Serialize(new { orderId = order.OrderId, paymentMethod = method, amount = order.TotalAmount, paidBy }),
            now));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Order {OrderId} paid ({PaymentMethod}) by {PaidBy}: invoice task {InvoiceTaskId}, e-mail task {EmailTaskId}",
            order.OrderId, method, paidBy, invoiceTask.TaskId, emailTask?.TaskId);

        return new PayOrderResponse(order.OrderId, order.PaymentStatus, now, invoiceTask.TaskId, emailTask?.TaskId);
    }

    /// <summary>
    /// A task of the order and its message. The message names the task and nothing else: its worker reads the order from
    /// the database. The outbox id is the RabbitMQ messageId; the task records it, so it is generated first.
    /// </summary>
    private ProcessingTask AddTask(
        ProcessingTaskType taskType,
        Guid orderId,
        string idempotencyKey,
        string eventType,
        Func<Guid, string> payloadOf,
        string correlationId)
    {
        var outboxEventId = Guid.CreateVersion7();
        var task = new ProcessingTask(taskType, orderId, correlationId, outboxEventId.ToString(), idempotencyKey);

        dbContext.ProcessingTasks.Add(task);
        dbContext.OutboxEvents.Add(new OutboxEvent(
            eventType,
            task.TaskId.ToString(),
            payloadOf(task.TaskId),
            correlationId,
            inventoryMovementId: null,
            outboxEventId));

        return task;
    }
}
