using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Inventory;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence.Configurations;

namespace Omnichannel.Infrastructure.Persistence.Inventories;

internal sealed class InventoryImportService(
    OmnichannelDbContext dbContext,
    ICurrentUser currentUser,
    ICorrelationContext correlationContext,
    TimeProvider timeProvider,
    ILogger<InventoryImportService> logger) : IInventoryImportService
{
    private const string SystemActor = "system";

    public async Task<ImportInventoryResponse> ImportAsync(ImportInventoryRequest request, CancellationToken cancellationToken)
    {
        var documentNumber = request.DocumentNumber.Trim();
        var documentSeries = string.IsNullOrWhiteSpace(request.DocumentSeries) ? null : request.DocumentSeries.Trim();
        var referenceId = documentSeries is null ? documentNumber : $"{documentSeries}/{documentNumber}";

        var now = timeProvider.GetUtcNow();
        var actor = currentUser.UserId ?? SystemActor;
        var correlationId = correlationContext.CorrelationId;

        // No EnableRetryOnFailure: a retried transaction would need the execution strategy around it.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Lock rows in one global order (ProductId) so concurrent multi-item imports cannot deadlock.
        // Every code path that locks several inventory rows must use this same ordering.
        var results = new Dictionary<Guid, ImportedInventoryItem>(request.Items.Count);
        foreach (var (item, index) in request.Items.Select((item, index) => (item, index)).OrderBy(x => x.item.ProductId))
        {
            // Single atomic statement: no read-modify-write in C#. Raw SQL bypasses the auditing interceptor,
            // so UpdatedAt/UpdatedBy are set here.
            List<StockRow> stock;
            try
            {
                stock = await dbContext.Database
                    .SqlQuery<StockRow>($"""
                        UPDATE "Inventories"
                        SET "TotalStock" = "TotalStock" + {item.Stock}, "UpdatedAt" = {now}, "UpdatedBy" = {actor}
                        WHERE "ProductId" = {item.ProductId}
                        RETURNING "TotalStock", "ReservedStock", "AvailableStock"
                        """)
                    .ToListAsync(cancellationToken);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.NumericValueOutOfRange)
            {
                // TotalStock is an integer column; the failed statement aborts the transaction, which is rolled back on dispose.
                throw new ConflictException(
                    InventoryErrorCodes.StockLimitExceeded,
                    $"Importing {item.Stock} units would exceed the maximum stock of product '{item.ProductId}'.");
            }

            if (stock.Count == 0)
            {
                // Disposing the transaction without commit rolls back the items already applied.
                throw new ValidationException(
                [
                    new ValidationFailure(
                        ValidationConventions.ItemPath(nameof(request.Items), index, nameof(ImportInventoryItem.ProductId)),
                        ValidationMessages.Get(ValidationMessages.Keys.ProductNotFound, ("ProductId", item.ProductId)))
                ]);
            }

            var snapshot = stock[0];
            var movement = new InventoryMovement(
                item.ProductId,
                InventoryMovementType.Import,
                item.Stock,
                InventoryReferenceType.StockImport,
                referenceId,
                documentNumber,
                documentSeries);

            var payload = new InventoryChangedPayload(
                item.ProductId,
                movement.InventoryMovementId,
                movement.Type,
                snapshot.TotalStock,
                snapshot.ReservedStock,
                snapshot.AvailableStock,
                now);

            dbContext.InventoryMovements.Add(movement);
            dbContext.OutboxEvents.Add(new OutboxEvent(
                EventTypes.InventoryChanged,
                item.ProductId.ToString(),
                MessageJson.Serialize(payload),
                correlationId,
                movement.InventoryMovementId));

            results[item.ProductId] = new ImportedInventoryItem(
                item.ProductId,
                movement.InventoryMovementId,
                snapshot.TotalStock,
                snapshot.ReservedStock,
                snapshot.AvailableStock);
        }

        // Audit row in the same transaction: it exists exactly when the import was applied.
        var totalQuantity = request.Items.Sum(i => (long)i.Stock);
        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Information,
            InventoryEvents.Imported,
            correlationId,
            taskId: null,
            orderId: null,
            workerName: null,
            MessageJson.Serialize(new { referenceId, itemCount = request.Items.Count, totalQuantity }),
            now));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(InventoryMovementConfiguration.ReferenceIndexName))
        {
            throw new ConflictException(
                InventoryErrorCodes.ImportAlreadyApplied,
                $"Stock import '{referenceId}' has already been applied.");
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Stock import {ReferenceId} applied: {ItemCount} items, {TotalQuantity} units",
            referenceId,
            request.Items.Count,
            totalQuantity);

        return new ImportInventoryResponse(referenceId, request.Items.Select(i => results[i.ProductId]).ToList());
    }

    private sealed class StockRow
    {
        public int TotalStock { get; init; }
        public int ReservedStock { get; init; }
        public int AvailableStock { get; init; }
    }
}
