using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Excel;
using Omnichannel.Infrastructure.Persistence.Alerts;
using Omnichannel.Infrastructure.Persistence.Inventories;
using Omnichannel.Infrastructure.Persistence.Orders;
using Omnichannel.Infrastructure.Persistence.Processing;

namespace Omnichannel.Infrastructure.Persistence.Reports;

/// <summary>
/// Persistence for the Report Export Worker: the attempt bookkeeping every worker shares, reading the rows of an export
/// from one snapshot while the workbook is built, and completing the export. The upload to the object storage happens
/// between <see cref="GenerateAsync"/> and <see cref="CompleteAsync"/>, in the worker, outside any transaction.
/// </summary>
internal sealed class ReportExportProcessingService(
    OmnichannelDbContext dbContext,
    ProcessingAttemptStore attempts,
    AlertStore alerts,
    IOptions<ReportsOptions> options,
    TimeProvider timeProvider) : IReportExportProcessingService
{
    public async Task<ReportExportTaskState?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await dbContext.ProcessingTasks
            .AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .LeftJoin(dbContext.ReportExports, t => t.TaskId, e => e.TaskId, (t, e) => new ReportExportTaskState(
                t.TaskId,
                t.TaskType,
                t.Status,
                t.CorrelationId,
                e != null,
                e != null && (e.DeletedAt != null || e.ExpiresAt <= now)))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<AttemptStart?> StartAttemptAsync(
        Guid taskId,
        string messageId,
        string workerName,
        string workerInstanceId,
        CancellationToken cancellationToken) =>
        attempts.StartAttemptAsync(taskId, messageId, workerName, workerInstanceId, cancellationToken);

    public async Task<GeneratedReport> GenerateAsync(Guid taskId, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        var export = await dbContext.ReportExports
            .AsNoTracking()
            .Where(e => e.TaskId == taskId)
            .Select(e => new { e.ReportType, e.Parameters, e.FileName, e.CreatedBy })
            .SingleAsync(cancellationToken);

        // Read-only and from one snapshot: an order committed while the file is built is in every sheet or in none.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

        var content = new MemoryStream();
        try
        {
            var rows = export.ReportType switch
            {
                ReportType.Orders => await WriteOrdersAsync(
                    Parameters<OrdersExportParameters>(export.Parameters), export.CreatedBy, content, cancellationToken),
                ReportType.Inventory => await WriteInventoryAsync(
                    Parameters<InventoryExportParameters>(export.Parameters), export.CreatedBy, content, cancellationToken),
                _ => throw new ReportExportRejectedException(
                    ReportErrorCodes.ParametersInvalid, $"Report type '{export.ReportType}' cannot be exported.")
            };

            await transaction.CommitAsync(cancellationToken);
            content.Position = 0;
            return new GeneratedReport(content, export.FileName, rows);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }
    }

    public async Task CompleteAsync(Guid taskId, Guid attemptId, long sizeBytes, int rowCount, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var task = await attempts.LockTaskAsync(taskId, cancellationToken);
        var completed = !task.IsTerminal;
        if (completed)
        {
            var export = await dbContext.ReportExports.SingleAsync(e => e.TaskId == taskId, cancellationToken);
            export.Complete(sizeBytes, rowCount, now);
            task.Succeed(orderId: null, attemptId, now);
        }
        else
        {
            // Another delivery of the same message finished first; this attempt stored the same file under the same key.
            task.CloseAttemptOfCompletedTask(attemptId, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (completed)
        {
            // The task succeeded: the alert of its earlier final failure (it was retried manually) is over.
            await alerts.ResolveAsync([AlertDedupKey.ProcessingFailed(taskId)], cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task RecordTransientFailureAsync(
        Guid taskId,
        Guid attemptId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken) =>
        attempts.RecordTransientFailureAsync(taskId, attemptId, errorCode, errorMessage, cancellationToken);

    public Task<int?> RecordFinalFailureAsync(
        Guid taskId,
        Guid? attemptId,
        string errorCode,
        string errorMessage,
        string workerName,
        string workerInstanceId,
        string queue,
        CancellationToken cancellationToken) =>
        attempts.RecordFinalFailureAsync(
            taskId, attemptId, errorCode, errorMessage, workerName, workerInstanceId, queue, cancellationToken);

    public async Task RecordRejectedMessageAsync(
        string? messageId,
        string? correlationId,
        Guid? taskId,
        MessageRejection rejection,
        string workerName,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Error,
            ReportEvents.MessageRejected,
            correlationId,
            taskId,
            orderId: null,
            workerName,
            MessageJson.Serialize(new { messageId, reasonCode = rejection.Code, reason = rejection.Reason }),
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <returns>Data rows of the file: orders plus item lines.</returns>
    private async Task<int> WriteOrdersAsync(
        OrdersExportParameters parameters,
        string? requesterId,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var timeZone = TimeZoneOf(parameters.TimeZone);
        var maxRows = options.Value.MaxRows;
        var orders = OrderFilters.Apply(dbContext.Orders.AsNoTracking(), parameters.ToFilter());

        using var workbook = new OrdersWorkbook(ReportLanguages.CultureOf(parameters.Language), timeZone, parameters.TimeZone);

        // A fixed order, so two exports of the same data are the same file: orders by creation time, then id.
        var orderRows = orders
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.OrderId)
            .Select(o => new OrderExportRow(
                o.CreatedAt,
                o.Channel.Name,
                o.ExternalOrderId,
                o.Customer.Name,
                o.Customer.Phone,
                o.Status,
                o.Items.Count,
                o.TotalAmount,
                o.OrderId))
            .AsAsyncEnumerable();
        await foreach (var order in orderRows.WithCancellation(cancellationToken))
        {
            EnsureBelowLimit(workbook.OrderRows + 1, maxRows);
            workbook.AddOrder(order);
        }

        // Item lines in the order of their orders, then by SKU (no line number is stored).
        var itemRows = orders
            .SelectMany(o => o.Items, (o, i) => new
            {
                o.CreatedAt,
                o.OrderId,
                o.ExternalOrderId,
                Channel = o.Channel.Name,
                i.Product.Sku,
                ProductName = i.Product.Name,
                i.Stock,
                i.UnitPrice
            })
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.OrderId)
            .ThenBy(x => x.Sku)
            .AsAsyncEnumerable();
        await foreach (var item in itemRows.WithCancellation(cancellationToken))
        {
            EnsureBelowLimit(workbook.OrderRows + workbook.ItemRows + 1, maxRows);
            workbook.AddItem(new OrderItemExportRow(
                item.CreatedAt,
                item.ExternalOrderId,
                item.Channel,
                item.Sku,
                item.ProductName,
                item.Stock,
                item.UnitPrice,
                item.Stock * item.UnitPrice,
                item.OrderId));
        }

        // The filters as entered, with names instead of ids (from the same snapshot).
        var channelName = parameters.Channel is null
            ? null
            : await dbContext.Channels.AsNoTracking()
                .Where(c => c.Slug == parameters.Channel)
                .Select(c => c.Name)
                .SingleOrDefaultAsync(cancellationToken) ?? parameters.Channel;
        var customerName = parameters.CustomerId is not { } customerId
            ? null
            : await dbContext.Customers.AsNoTracking()
                .Where(c => c.CustomerId == customerId)
                .Select(c => c.Name)
                .SingleOrDefaultAsync(cancellationToken) ?? customerId.ToString();

        workbook.Save(
            new OrdersExportInfo(
                channelName,
                parameters.Status,
                customerName,
                parameters.Search,
                parameters.From,
                parameters.To,
                timeProvider.GetUtcNow(),
                await RequesterEmailAsync(requesterId, cancellationToken)),
            destination);

        return workbook.OrderRows + workbook.ItemRows;
    }

    /// <returns>Data rows of the file: products.</returns>
    private async Task<int> WriteInventoryAsync(
        InventoryExportParameters parameters,
        string? requesterId,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var timeZone = TimeZoneOf(parameters.TimeZone);
        var maxRows = options.Value.MaxRows;

        using var workbook = new InventoryWorkbook(ReportLanguages.CultureOf(parameters.Language), timeZone, parameters.TimeZone);

        var productRows = InventoryFilters.Apply(dbContext.Inventories.AsNoTracking(), parameters.ToFilter())
            .OrderBy(i => i.Product.Sku)
            .Select(InventoryPredicates.Inline<Inventory, InventoryExportRow>(
                (i, isLowStock) => new InventoryExportRow(
                    i.Product.Sku,
                    i.Product.Name,
                    i.Product.Price,
                    i.TotalStock,
                    i.ReservedStock,
                    i.AvailableStock,
                    i.Product.LowStockThreshold,
                    isLowStock,
                    i.Product.ExpiryDate,
                    i.UpdatedAt ?? i.CreatedAt),
                InventoryPredicates.IsLowStock))
            .AsAsyncEnumerable();
        await foreach (var product in productRows.WithCancellation(cancellationToken))
        {
            EnsureBelowLimit(workbook.ProductRows + 1, maxRows);
            workbook.AddProduct(product);
        }

        workbook.Save(
            new InventoryExportInfo(
                parameters.Search,
                parameters.LowStockOnly,
                timeProvider.GetUtcNow(),
                await RequesterEmailAsync(requesterId, cancellationToken)),
            destination);

        return workbook.ProductRows;
    }

    /// <summary>The data grew after the request's count: the file would be larger than an export may be.</summary>
    private static void EnsureBelowLimit(int rows, int maxRows)
    {
        if (rows > maxRows)
        {
            throw new ReportExportRejectedException(
                ReportErrorCodes.ExportTooLarge, $"The export has more than {maxRows} rows.");
        }
    }

    private static TParameters Parameters<TParameters>(string json)
    {
        try
        {
            return MessageJson.Deserialize<TParameters>(json);
        }
        catch (JsonException)
        {
            throw new ReportExportRejectedException(ReportErrorCodes.ParametersInvalid, "The stored parameters of the export cannot be read.");
        }
    }

    private static TimeZoneInfo TimeZoneOf(string? id) =>
        ReportTimeZones.TryFind(id, out var timeZone)
            ? timeZone
            : throw new ReportExportRejectedException(
                ReportErrorCodes.ParametersInvalid, $"Time zone '{id}' of the export is not known on this host.");

    /// <summary>The e-mail of the requester; null when the id is no user id or the user does not exist any more.</summary>
    private async Task<string?> RequesterEmailAsync(string? requesterId, CancellationToken cancellationToken) =>
        Guid.TryParse(requesterId, out var userId)
            ? await dbContext.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).SingleOrDefaultAsync(cancellationToken)
            : null;
}
