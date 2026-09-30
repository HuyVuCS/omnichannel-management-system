using Omnichannel.Application.Common.Abstractions;

namespace Omnichannel.Workers;

/// <summary>
/// Audit identity for rows written by background workers (CreatedBy/UpdatedBy). One per DI scope; a worker sets its own
/// identity (e.g. <c>system:order-worker</c>) at the start of each unit of work.
/// </summary>
public sealed class WorkerCurrentUser : ICurrentUser
{
    public const string DefaultActor = "system:workers";

    public string? UserId { get; set; } = DefaultActor;
}
