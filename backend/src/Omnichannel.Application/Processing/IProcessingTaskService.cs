using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Processing;

/// <summary>Processing tasks for the dashboard (doc §5.3, §11): search, detail, manual retry.</summary>
public interface IProcessingTaskService
{
    Task<PagedResult<ProcessingTaskSummaryResponse>> ListAsync(ProcessingTaskListQuery query, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException"><c>task_not_found</c>.</exception>
    Task<ProcessingTaskDetailResponse> GetAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>
    /// Manual retry of a <c>Failed</c> task (doc §3.4), in one transaction: the task row is locked, a new outbox row copies the
    /// original message (same event type, aggregate, payload and correlation id; original routing key), the task becomes
    /// <c>Pending</c> with the new message id and a fresh attempt budget (attempts are kept), and a
    /// <see cref="ProcessingEvents.RetryRequested"/> audit event is written. The Outbox Publisher and the worker take it from there.
    /// A <c>StockSync</c> task is not retried with a copy of its source message (an <c>inventory.changed</c> that every channel
    /// receives): it gets a new <c>stock.sync.requested</c> directed at its own channel.
    /// </summary>
    /// <exception cref="Common.Exceptions.NotFoundException"><c>task_not_found</c>.</exception>
    /// <exception cref="Common.Exceptions.ConflictException"><c>task_not_failed</c>, <c>retry_source_missing</c>.</exception>
    Task<RetryProcessingTaskResponse> RetryAsync(Guid taskId, CancellationToken cancellationToken);
}
