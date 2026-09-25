namespace Omnichannel.Domain.Enums;

public enum ProcessingTaskStatus
{
    Pending,
    InProgress,
    /// <summary>Last attempt failed with a temporary error; another attempt is scheduled.</summary>
    RetryScheduled,
    Succeeded,
    /// <summary>Terminal: retry limit reached or non-retryable error; message is in the DLQ.</summary>
    Failed
}
