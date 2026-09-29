namespace Omnichannel.Workers;

/// <summary>Bound from the <c>Worker</c> configuration section.</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>Identifies this Workers process in logs and processing history. Default: <c>{MachineName}-{ProcessId}</c>.</summary>
    public string InstanceId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";
}
