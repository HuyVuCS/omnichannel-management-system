using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Test seam: makes the first N calls of a reservation-service method (or of the retry publisher) throw, then behaves normally.
/// Counters are shared by every scope of the host, so "N times" means N deliveries.
/// </summary>
public sealed class FailureInjection
{
    private int _reserveCalls;
    private int _startAttemptCalls;
    private int _retryPublishCalls;
    private volatile bool _healed;

    /// <summary>Failing <c>ReserveAsync</c> calls (<see cref="int.MaxValue"/> = always).</summary>
    public int ReserveFailures { get; init; }

    public Func<Exception> ReserveException { get; init; } = () => new TimeoutException("injected transient failure");

    /// <summary>Failing <c>StartAttemptAsync</c> calls — escapes the handler (crash loop).</summary>
    public int StartAttemptFailures { get; init; }

    /// <summary>Failing retry-copy publishes.</summary>
    public int RetryPublishFailures { get; init; }

    public int ReserveCalls => Volatile.Read(ref _reserveCalls);

    public int StartAttemptCalls => Volatile.Read(ref _startAttemptCalls);

    public int RetryPublishCalls => Volatile.Read(ref _retryPublishCalls);

    public bool Triggered => ReserveCalls > 0 && ReserveFailures > 0;

    /// <summary>
    /// Only this task is affected (calls for other tasks are neither failed nor counted); null = every task. Needed when the
    /// Workers host runs against the shared API database, where other tests leave order tasks behind.
    /// </summary>
    public Guid? OnlyTaskId { get; init; }

    /// <summary>From now on nothing fails any more (the injected fault is "fixed").</summary>
    public void Heal() => _healed = true;

    internal bool NextReserveFails(Guid taskId) =>
        !_healed && Affects(taskId) && Interlocked.Increment(ref _reserveCalls) <= ReserveFailures;

    internal bool NextStartAttemptFails(Guid taskId) =>
        !_healed && Affects(taskId) && Interlocked.Increment(ref _startAttemptCalls) <= StartAttemptFailures;

    private bool Affects(Guid taskId) => OnlyTaskId is not { } only || only == taskId;

    internal bool NextRetryPublishFails() => !_healed && Interlocked.Increment(ref _retryPublishCalls) <= RetryPublishFailures;
}

internal static class FailureInjectionExtensions
{
    public static IServiceCollection InjectFailures(this IServiceCollection services, FailureInjection failures)
    {
        var reservations = services.Single(d => d.ServiceType == typeof(IOrderReservationService));
        services.Remove(reservations);
        services.AddScoped<IOrderReservationService>(sp => new FailingReservationService(
            (IOrderReservationService)ActivatorUtilities.CreateInstance(sp, reservations.ImplementationType!),
            failures));

        var publisher = services.Single(d => d.ServiceType == typeof(IRetryPublisher));
        services.Remove(publisher);
        services.AddSingleton<IRetryPublisher>(sp => new FailingRetryPublisher(
            (IRetryPublisher)ActivatorUtilities.CreateInstance(sp, publisher.ImplementationType!),
            failures));

        return services;
    }

    private sealed class FailingRetryPublisher(IRetryPublisher inner, FailureInjection failures) : IRetryPublisher
    {
        public Task PublishDelayedAsync(
            string queue, ReceivedMessage message, int retryNumber, IReadOnlyDictionary<string, object?> headers, CancellationToken cancellationToken) =>
            failures.NextRetryPublishFails()
                ? throw new InvalidOperationException("injected retry publish failure")
                : inner.PublishDelayedAsync(queue, message, retryNumber, headers, cancellationToken);
    }

    private sealed class FailingReservationService(IOrderReservationService inner, FailureInjection failures) : IOrderReservationService
    {
        public Task<ProcessingTaskStatus?> GetTaskStatusAsync(Guid taskId, CancellationToken cancellationToken) =>
            inner.GetTaskStatusAsync(taskId, cancellationToken);

        public Task<AttemptStart?> StartAttemptAsync(Guid taskId, string workerName, string workerInstanceId, CancellationToken cancellationToken) =>
            failures.NextStartAttemptFails(taskId)
                ? throw new InvalidOperationException("injected crash before the attempt started")
                : inner.StartAttemptAsync(taskId, workerName, workerInstanceId, cancellationToken);

        public Task<ReservationResult> ReserveAsync(OrderCreatedPayload order, Guid attemptId, string workerName, CancellationToken cancellationToken) =>
            failures.NextReserveFails(order.TaskId)
                ? throw failures.ReserveException()
                : inner.ReserveAsync(order, attemptId, workerName, cancellationToken);

        public Task RecordTransientFailureAsync(Guid taskId, Guid attemptId, string errorCode, string errorMessage, CancellationToken cancellationToken) =>
            inner.RecordTransientFailureAsync(taskId, attemptId, errorCode, errorMessage, cancellationToken);

        public Task<int?> RecordFinalFailureAsync(
            Guid taskId, Guid? attemptId, string errorCode, string errorMessage, string workerName, string workerInstanceId, string queue,
            CancellationToken cancellationToken) =>
            inner.RecordFinalFailureAsync(taskId, attemptId, errorCode, errorMessage, workerName, workerInstanceId, queue, cancellationToken);

        public Task RecordRejectedMessageAsync(string? messageId, string? correlationId, Guid? taskId, string reason, string workerName, CancellationToken cancellationToken) =>
            inner.RecordRejectedMessageAsync(messageId, correlationId, taskId, reason, workerName, cancellationToken);
    }
}
