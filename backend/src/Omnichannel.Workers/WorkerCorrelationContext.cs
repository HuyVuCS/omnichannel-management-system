using Omnichannel.Application.Common.Abstractions;

namespace Omnichannel.Workers;

/// <summary>
/// Correlation id for one unit of background work (one DI scope). Message handlers (step 6+) set it from the
/// message envelope before doing any work; otherwise a new id is used.
/// </summary>
public sealed class WorkerCorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
}
