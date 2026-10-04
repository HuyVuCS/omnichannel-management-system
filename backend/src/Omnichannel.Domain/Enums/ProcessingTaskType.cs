namespace Omnichannel.Domain.Enums;

public enum ProcessingTaskType
{
    OrderProcessing,
    StockSync,
    Invoice,
    Email,

    /// <summary>An Excel export built in the background; its request and result are the task's <c>ReportExport</c> row.</summary>
    ReportExport
}
