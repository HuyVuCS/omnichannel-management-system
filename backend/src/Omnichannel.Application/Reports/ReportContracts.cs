using System.Globalization;
using System.Text.Json.Nodes;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Reports;

/// <summary>
/// An orders export: the filters of <c>GET /api/orders</c> with the same meaning. <see cref="From"/> and <see cref="To"/>
/// are required (inclusive, on the order's creation time, at most <c>Reports:MaxRangeDays</c> apart).
/// </summary>
/// <param name="TimeZone">IANA id (<c>Asia/Ho_Chi_Minh</c>): the times in the file and its name are in this zone.</param>
public sealed record ExportOrdersRequest(
    string? Channel,
    OrderStatus? Status,
    Guid? CustomerId,
    string? Search,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? TimeZone);

/// <summary>An inventory export: the filters of <c>GET /api/inventory</c> with the same meaning.</summary>
/// <param name="TimeZone">IANA id: the times in the file and its name are in this zone.</param>
public sealed record ExportInventoryRequest(string? Search, bool LowStockOnly, string? TimeZone);

/// <summary>The export was accepted; it is built in the background by the task with this id.</summary>
public sealed record ExportAcceptedResponse(Guid TaskId);

/// <summary>The status of the export's task, or <see cref="Expired"/> once its file was deleted.</summary>
public enum ReportExportStatus
{
    Pending,
    InProgress,
    RetryScheduled,
    Succeeded,
    Failed,

    /// <summary>The retention time is over and the file is gone.</summary>
    Expired
}

/// <param name="Parameters">The normalised request: the filters, <c>timeZone</c> and <c>language</c>.</param>
/// <param name="RowCount">Data rows in the file (orders and item lines, or products); null until the file exists.</param>
/// <param name="ErrorCode">The code of the last attempt when the export failed; null otherwise. Never an error text.</param>
public sealed record ReportExportResponse(
    Guid TaskId,
    ReportType ReportType,
    ReportExportStatus Status,
    JsonObject? Parameters,
    string FileName,
    int? RowCount,
    long? SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset ExpiresAt,
    string? ErrorCode);

/// <param name="Url">A link that reads the file without the API's token. A credential until <paramref name="ExpiresAt"/>.</param>
public sealed record ReportDownloadResponse(string Url, DateTimeOffset ExpiresAt, string FileName);

/// <summary>The caller's own exports, newest first unless <c>sortDir=asc</c>. <c>search</c> is not supported (ignored).</summary>
public sealed record ReportExportListQuery : PageQuery
{
    public const string SortByCreatedAt = "createdAt";

    public static readonly IReadOnlyList<string> SortFields = [SortByCreatedAt];

    public ReportType? ReportType { get; init; }
}

/// <summary>What an orders export was asked for, as stored with it (<c>ReportExports.Parameters</c>).</summary>
/// <param name="Channel">Channel slug, trimmed and lower-case; null = every channel.</param>
/// <param name="Search">Trimmed; null = no search.</param>
/// <param name="From">UTC.</param>
/// <param name="To">UTC.</param>
/// <param name="Language"><c>vi</c> or <c>en</c>: the language of the request, used for the texts of the file.</param>
public sealed record OrdersExportParameters(
    string? Channel,
    OrderStatus? Status,
    Guid? CustomerId,
    string? Search,
    DateTimeOffset From,
    DateTimeOffset To,
    string TimeZone,
    string Language)
{
    /// <summary>The request in its stored form. The request has passed its validator: range and time zone are present.</summary>
    public static OrdersExportParameters Normalize(ExportOrdersRequest request, string language) =>
        new(
            string.IsNullOrWhiteSpace(request.Channel) ? null : request.Channel.Trim().ToLowerInvariant(),
            request.Status,
            request.CustomerId,
            string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim(),
            request.From!.Value.ToUniversalTime(),
            request.To!.Value.ToUniversalTime(),
            request.TimeZone!.Trim(),
            language);

    public OrderFilter ToFilter() => new(Channel, Status, CustomerId, From, To, Search);
}

/// <summary>What an inventory export was asked for, as stored with it.</summary>
public sealed record InventoryExportParameters(string? Search, bool LowStockOnly, string TimeZone, string Language)
{
    public static InventoryExportParameters Normalize(ExportInventoryRequest request, string language) =>
        new(
            string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim(),
            request.LowStockOnly,
            request.TimeZone!.Trim(),
            language);

    public InventoryFilter ToFilter() => new(Search, LowStockOnly);
}

/// <summary>The languages a file can be written in.</summary>
public static class ReportLanguages
{
    public const string English = "en";
    public const string Vietnamese = "vi";

    /// <summary>The language of the current request (its UI culture, from <c>Accept-Language</c>); English for any other.</summary>
    public static string Current() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Vietnamese ? Vietnamese : English;

    /// <summary>The culture whose texts a file in <paramref name="language"/> uses.</summary>
    public static CultureInfo CultureOf(string? language) =>
        language == Vietnamese ? CultureInfo.GetCultureInfo(Vietnamese) : CultureInfo.GetCultureInfo(English);
}

/// <summary>Time zones of a request: an IANA id the host knows.</summary>
public static class ReportTimeZones
{
    public const int MaxIdLength = 64;

    public static bool IsKnown(string? id) => TryFind(id, out _);

    public static bool TryFind(string? id, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        return !string.IsNullOrWhiteSpace(id)
            && id.Length <= MaxIdLength
            && TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out timeZone!);
    }
}

/// <summary>The name an export is downloaded as: <c>orders_20261005-1030.xlsx</c> - the local time of the request, ASCII only.</summary>
public static class ReportFileName
{
    public static string For(ReportType reportType, DateTimeOffset requestedAt, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(requestedAt, timeZone);
        return $"{reportType.ToString().ToLowerInvariant()}_{local.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.xlsx";
    }
}

public static class ReportErrorCodes
{
    public const string ExportNotFound = "export_not_found";

    /// <summary>409: the user already has <c>Reports:MaxUnfinishedPerUser</c> exports waiting or running.</summary>
    public const string TooManyExports = "too_many_exports";

    /// <summary>
    /// More rows than <c>Reports:MaxRows</c>: 400 at the request; at the worker the task fails with it (the data grew
    /// after the request was accepted).
    /// </summary>
    public const string ExportTooLarge = "export_too_large";

    /// <summary>409: a download was asked for before the export succeeded (or after it failed).</summary>
    public const string ExportNotReady = "export_not_ready";

    /// <summary>
    /// The retention time is over: 409 for a download once the file was deleted; at the worker the task fails with it
    /// instead of building a file nobody could keep.
    /// </summary>
    public const string ExportExpired = "export_expired";

    /// <summary>Worker: the stored parameters cannot be read on this host (unknown time zone, broken JSON).</summary>
    public const string ParametersInvalid = "export_parameters_invalid";
}

/// <summary>SystemEventLog event types written by exports.</summary>
public static class ReportEvents
{
    /// <summary>A user asked for an export (Information, audit of a bulk data extraction). Payload: <c>{reportType, requestedBy}</c>.</summary>
    public const string ExportRequested = "report.export_requested";

    /// <summary>A message on the export queue could never be processed and went to the DLQ. Payload: <c>{messageId, reasonCode, reason}</c>.</summary>
    public const string MessageRejected = "report_export.message_rejected";
}

/// <summary>The file content type of every export.</summary>
public static class ReportFiles
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The header that makes a browser save the file under its name instead of opening it.</summary>
    public static string ContentDispositionOf(string fileName) => $"attachment; filename=\"{fileName}\"";
}
