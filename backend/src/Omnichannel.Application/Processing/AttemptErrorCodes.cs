namespace Omnichannel.Application.Processing;

/// <summary><c>ErrorCode</c> values of a failed attempt that more than one project names (the workers' classifier has the full list).</summary>
public static class AttemptErrorCodes
{
    /// <summary>A failure nothing recognises: not an answer of a channel, the database or the storage - a defect until shown otherwise.</summary>
    public const string Unexpected = "unexpected";

    /// <summary>
    /// Written by the stale-task sweep, never by a worker: the task was not finished and nothing changed it for
    /// <c>StaleTasks:StaleAfter</c> - its message is lost or was never published. A manual retry sends a new one.
    /// </summary>
    public const string StaleNoProgress = "stale_no_progress";
}
