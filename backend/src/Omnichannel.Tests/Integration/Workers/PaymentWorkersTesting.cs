using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Invoices;
using Omnichannel.Application.Notifications;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Infrastructure.ExternalProviders;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Test seam of a Workers host for the payment workers: records every call that reaches the simulated invoice provider
/// and e-mail sender (behind their production timeout and circuit breaker), and can interfere with the completion of an
/// invoice task - fail it after the provider answered, or do something right before it.
/// </summary>
public sealed class PaymentProbe
{
    private readonly ConcurrentQueue<InvoiceRequest> _invoiceRequests = new();
    private readonly ConcurrentQueue<EmailMessage> _emails = new();
    private int _completionFailures;

    /// <summary>Failing <c>CompleteAsync</c> calls of the invoice task <see cref="OnlyTaskId"/> (the provider has answered by then).</summary>
    public int InvoiceCompletionFailures { get; init; }

    /// <summary>Runs right before each completion of the invoice task <see cref="OnlyTaskId"/>.</summary>
    public Func<Task>? BeforeInvoiceCompletion { get; init; }

    /// <summary>Called when a call for the order arrives at the provider; the call goes on when the task it returns ends.</summary>
    public Func<InvoiceRequest, CancellationToken, Task>? OnInvoiceRequest { get; init; }

    public Guid? OnlyTaskId { get; init; }

    public IReadOnlyList<InvoiceRequest> InvoiceRequestsOf(Guid orderId) => _invoiceRequests.Where(r => r.OrderId == orderId).ToList();

    public IReadOnlyList<EmailMessage> EmailsOf(Guid orderId) => _emails.Where(m => m.Key == PaymentTaskKeys.ForEmail(orderId)).ToList();

    internal void Requested(InvoiceRequest request) => _invoiceRequests.Enqueue(request);

    internal void Sending(EmailMessage message) => _emails.Enqueue(message);

    internal bool Affects(Guid taskId) => OnlyTaskId is not { } only || only == taskId;

    internal bool NextCompletionFails(Guid taskId) => Affects(taskId) && Interlocked.Increment(ref _completionFailures) <= InvoiceCompletionFailures;
}

internal static class PaymentProbeExtensions
{
    public static IServiceCollection ProbePayments(this IServiceCollection services, PaymentProbe probe)
    {
        var provider = services.Single(d =>
            d.ServiceType == typeof(IInvoiceProviderClient) && d.IsKeyedService && Equals(d.ServiceKey, ResilientInvoiceProviderClient.AdapterKey));
        services.Remove(provider);
        services.AddKeyedSingleton<IInvoiceProviderClient>(
            ResilientInvoiceProviderClient.AdapterKey,
            (sp, _) => new RecordingProvider(ActivatorUtilities.CreateInstance<SimulatedInvoiceProviderClient>(sp), probe));

        var sender = services.Single(d =>
            d.ServiceType == typeof(IEmailSender) && d.IsKeyedService && Equals(d.ServiceKey, ResilientEmailSender.AdapterKey));
        services.Remove(sender);
        services.AddKeyedSingleton<IEmailSender>(
            ResilientEmailSender.AdapterKey,
            (sp, _) => new RecordingSender(ActivatorUtilities.CreateInstance<SimulatedEmailSender>(sp), probe));

        var processing = services.Single(d => d.ServiceType == typeof(IInvoiceProcessingService));
        services.Remove(processing);
        services.AddScoped<IInvoiceProcessingService>(sp => new InterferingInvoices(
            (IInvoiceProcessingService)ActivatorUtilities.CreateInstance(sp, processing.ImplementationType!), probe));

        return services;
    }

    private sealed class RecordingProvider(IInvoiceProviderClient inner, PaymentProbe probe) : IInvoiceProviderClient
    {
        public async Task<IssuedInvoice> IssueAsync(InvoiceRequest request, CancellationToken cancellationToken)
        {
            probe.Requested(request);
            if (probe.OnInvoiceRequest is { } onRequest)
            {
                await onRequest(request, cancellationToken);
            }

            return await inner.IssueAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingSender(IEmailSender inner, PaymentProbe probe) : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            probe.Sending(message);
            return inner.SendAsync(message, cancellationToken);
        }
    }

    private sealed class InterferingInvoices(IInvoiceProcessingService inner, PaymentProbe probe) : IInvoiceProcessingService
    {
        public Task<TaskRequestState?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) => inner.GetTaskAsync(taskId, cancellationToken);

        public Task<AttemptStart?> StartAttemptAsync(Guid taskId, string messageId, string workerName, string workerInstanceId, CancellationToken cancellationToken) =>
            inner.StartAttemptAsync(taskId, messageId, workerName, workerInstanceId, cancellationToken);

        public Task<InvoiceOrder> ReadOrderAsync(Guid orderId, CancellationToken cancellationToken) => inner.ReadOrderAsync(orderId, cancellationToken);

        public async Task CompleteAsync(Guid taskId, Guid attemptId, Invoice? invoice, CancellationToken cancellationToken)
        {
            if (probe.Affects(taskId) && probe.BeforeInvoiceCompletion is { } before)
            {
                await before();
            }

            if (probe.NextCompletionFails(taskId))
            {
                throw new TimeoutException("injected failure after the provider answered, before the commit");
            }

            await inner.CompleteAsync(taskId, attemptId, invoice, cancellationToken);
        }

        public Task RecordTransientFailureAsync(Guid taskId, Guid attemptId, string errorCode, string errorMessage, CancellationToken cancellationToken) =>
            inner.RecordTransientFailureAsync(taskId, attemptId, errorCode, errorMessage, cancellationToken);

        public Task<int?> RecordFinalFailureAsync(
            Guid taskId, Guid? attemptId, string errorCode, string errorMessage, string workerName, string workerInstanceId, string queue,
            CancellationToken cancellationToken) =>
            inner.RecordFinalFailureAsync(taskId, attemptId, errorCode, errorMessage, workerName, workerInstanceId, queue, cancellationToken);

        public Task RecordRejectedMessageAsync(
            string? messageId, string? correlationId, Guid? taskId, MessageRejection rejection, string workerName, CancellationToken cancellationToken) =>
            inner.RecordRejectedMessageAsync(messageId, correlationId, taskId, rejection, workerName, cancellationToken);
    }
}
