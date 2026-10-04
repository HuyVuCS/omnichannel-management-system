namespace Omnichannel.Application.Common.Messaging;

/// <summary>
/// Payload of <see cref="EventTypes.ReportExportRequested"/>: the export task to run. What to export is not part of the
/// message; the worker reads the parameters from the task's <c>ReportExports</c> row.
/// </summary>
public sealed record ReportExportRequestedPayload(Guid TaskId);
