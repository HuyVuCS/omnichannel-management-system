namespace Omnichannel.Application.Processing;

/// <summary><c>ErrorCode</c> values of a failed attempt that more than one project names (the workers' classifier has the full list).</summary>
public static class AttemptErrorCodes
{
    /// <summary>A failure nothing recognises: not an answer of a channel, the database or the storage - a defect until shown otherwise.</summary>
    public const string Unexpected = "unexpected";
}
