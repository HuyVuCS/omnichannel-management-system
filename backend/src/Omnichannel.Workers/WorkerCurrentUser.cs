using Omnichannel.Application.Common.Abstractions;

namespace Omnichannel.Workers;

/// <summary>Audit identity for rows written by background workers.</summary>
public sealed class WorkerCurrentUser : ICurrentUser
{
    public string? UserId => "system:workers";
}
