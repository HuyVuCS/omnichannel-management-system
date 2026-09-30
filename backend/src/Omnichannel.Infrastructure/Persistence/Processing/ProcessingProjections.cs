using System.Linq.Expressions;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Processing;

/// <summary>
/// Projections shared by the order and task read services. Use inside other projections as
/// <c>t.Attempts.AsQueryable().Select(ToAttempt)</c> so EF Core translates it into the same SQL statement.
/// </summary>
internal static class ProcessingProjections
{
    public static readonly Expression<Func<ProcessingAttempt, ProcessingAttemptResponse>> ToAttempt =
        a => new ProcessingAttemptResponse(
            a.AttemptNo,
            a.Status,
            a.WorkerName,
            a.WorkerInstanceId,
            a.ErrorCode,
            a.ErrorMessage,
            a.StartedAt,
            a.FinishedAt);
}
