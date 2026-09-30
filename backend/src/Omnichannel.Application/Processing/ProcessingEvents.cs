namespace Omnichannel.Application.Processing;

/// <summary>SystemEventLog event types shared by every asynchronous task type.</summary>
public static class ProcessingEvents
{
    /// <summary>
    /// A processing task failed for good (retry limit, permanent error, delivery limit) and its message went to the DLQ.
    /// Payload: <c>{taskId, taskType, attemptNo, errorCode, errorMessage, queue}</c>.
    /// </summary>
    public const string Failed = "processing.failed";
}
