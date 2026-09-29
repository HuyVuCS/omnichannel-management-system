using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Integration;

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class InventoryImportTests(ApiFactory factory)
{
    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);

    [Fact]
    public async Task Concurrent_imports_for_one_product_are_all_applied_with_movement_and_outbox_each()
    {
        var product = await CreateProductAsync();
        var prefix = $"GRN-{TestData.UniqueSuffix()}";

        var responses = await Task.WhenAll(Enumerable.Range(1, 20).Select(n =>
            _manager.PostAsJsonAsync("/api/inventory/import", Import($"{prefix}-{n}", null, (product.ProductId, 1)))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var (totalStock, movements, outbox) = await factory.QueryDbAsync(async db =>
        {
            var inventory = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == product.ProductId);
            var movementIds = await db.InventoryMovements
                .Where(m => m.ProductId == product.ProductId)
                .Select(m => m.InventoryMovementId)
                .ToListAsync();
            var events = await db.OutboxEvents.AsNoTracking()
                .Where(e => e.AggregateId == product.ProductId.ToString())
                .ToListAsync();
            return (inventory.TotalStock, movementIds, events);
        });

        Assert.Equal(20, totalStock);
        Assert.Equal(20, movements.Count);
        Assert.Equal(20, outbox.Count);
        Assert.All(outbox, e =>
        {
            Assert.Equal(OutboxEventStatus.Pending, e.Status);
            Assert.Equal(EventTypes.InventoryChanged, e.EventType);
        });
        Assert.Equal(movements.Order(), outbox.Select(e => e.InventoryMovementId!.Value).Order());
    }

    [Fact]
    public async Task Reposting_the_same_document_is_rejected_and_changes_nothing()
    {
        var product = await CreateProductAsync();
        var request = Import($"GRN-{TestData.UniqueSuffix()}", "2026", (product.ProductId, 7));

        var first = await _manager.PostAsJsonAsync("/api/inventory/import", request);
        var before = await CountsAsync(product.ProductId);
        var second = await _manager.PostAsJsonAsync("/api/inventory/import", request);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal($"2026/{request.DocumentNumber}", (await first.ReadAsync<ImportInventoryResponse>()).ReferenceId);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(InventoryErrorCodes.ImportAlreadyApplied, await second.ReadErrorCodeAsync());
        Assert.Equal(before, await CountsAsync(product.ProductId));
        Assert.Equal((7, 1, 1), before);
    }

    [Fact]
    public async Task Import_with_an_unknown_product_is_rolled_back_completely()
    {
        var product = await CreateProductAsync();
        var unknown = Guid.NewGuid();

        var response = await _manager.PostAsJsonAsync(
            "/api/inventory/import",
            Import($"GRN-{TestData.UniqueSuffix()}", null, (product.ProductId, 5), (unknown, 5)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ReadErrorCodeAsync());
        Assert.Contains("\"items[1].productId\"", await response.Content.ReadAsStringAsync());
        Assert.Equal((0, 0, 0), await CountsAsync(product.ProductId));
    }

    [Fact]
    public async Task Validation_error_keys_are_json_paths()
    {
        var product = await CreateProductAsync();

        var unknownProduct = await _manager.PostAsJsonAsync(
            "/api/inventory/import",
            Import($"GRN-{TestData.UniqueSuffix()}", null, (product.ProductId, 1), (Guid.NewGuid(), 1)));
        var invalidFields = await _manager.PostAsJsonAsync(
            "/api/inventory/import",
            Import("", null, (product.ProductId, 0)));

        Assert.Equal(new[] { "items[1].productId" }, await ErrorKeysAsync(unknownProduct));
        Assert.Equal(new[] { "documentNumber", "items[0].stock" }, await ErrorKeysAsync(invalidFields));
    }

    [Fact]
    public async Task Import_beyond_the_maximum_stock_is_a_conflict_and_changes_nothing()
    {
        var product = await CreateProductAsync();
        const int nearMaximum = int.MaxValue - 10;
        await factory.QueryDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE \"Inventories\" SET \"TotalStock\" = {nearMaximum} WHERE \"ProductId\" = {product.ProductId}"));

        var response = await _manager.PostAsJsonAsync(
            "/api/inventory/import",
            Import($"GRN-{TestData.UniqueSuffix()}", null, (product.ProductId, 1_000_000)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(InventoryErrorCodes.StockLimitExceeded, await response.ReadErrorCodeAsync());
        Assert.Equal((nearMaximum, 0, 0), await CountsAsync(product.ProductId));
    }

    [Fact]
    public async Task Concurrent_multi_item_imports_in_opposite_order_do_not_deadlock()
    {
        var a = await CreateProductAsync();
        var b = await CreateProductAsync();
        var prefix = $"GRN-{TestData.UniqueSuffix()}";

        // Several rounds make an unordered lock acquisition very likely to deadlock at least once.
        for (var round = 0; round < 5; round++)
        {
            var responses = await Task.WhenAll(
                _manager.PostAsJsonAsync("/api/inventory/import",
                    Import($"{prefix}-ab-{round}", null, (a.ProductId, 1), (b.ProductId, 2))),
                _manager.PostAsJsonAsync("/api/inventory/import",
                    Import($"{prefix}-ba-{round}", null, (b.ProductId, 2), (a.ProductId, 1))));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        }

        Assert.Equal(10, (await CountsAsync(a.ProductId)).TotalStock);
        Assert.Equal(20, (await CountsAsync(b.ProductId)).TotalStock);
    }

    [Fact]
    public async Task Outbox_payload_is_a_stock_snapshot_carrying_the_request_correlation_id()
    {
        var product = await CreateProductAsync();
        var correlationId = $"corr-{TestData.UniqueSuffix()}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/import")
        {
            Content = JsonContent.Create(Import($"GRN-{TestData.UniqueSuffix()}", null, (product.ProductId, 12)))
        };
        request.Headers.Add("X-Correlation-Id", correlationId);

        var response = await _manager.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await response.ReadAsync<ImportInventoryResponse>()).Items);
        Assert.Equal((product.ProductId, 12, 0, 12), (item.ProductId, item.TotalStock, item.ReservedStock, item.AvailableStock));

        var outbox = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking()
            .SingleAsync(e => e.InventoryMovementId == item.MovementId));
        var payload = MessageJson.Deserialize<InventoryChangedPayload>(outbox.Payload);

        Assert.Equal(correlationId, outbox.CorrelationId);
        Assert.Equal(product.ProductId.ToString(), outbox.AggregateId);
        Assert.Equal(
            (product.ProductId, item.MovementId, InventoryMovementType.Import, 12, 0, 12),
            (payload.ProductId, payload.MovementId, payload.MovementType, payload.TotalStock, payload.ReservedStock, payload.AvailableStock));
        Assert.True(payload.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Movements_are_filtered_and_paged_newest_first()
    {
        var product = await CreateProductAsync();
        var prefix = $"GRN-{TestData.UniqueSuffix()}";
        for (var n = 1; n <= 3; n++)
        {
            var imported = await _manager.PostAsJsonAsync("/api/inventory/import", Import($"{prefix}-{n}", "S1", (product.ProductId, n)));
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        }

        var staff = factory.CreateClientAs(AppRoles.Staff);
        var firstPage = await staff.GetFromJsonAsync<PagedResult<InventoryMovementResponse>>(
            $"/api/inventory/movements?productId={product.ProductId}&pageSize=2", TestData.Json);
        var secondPage = await staff.GetFromJsonAsync<PagedResult<InventoryMovementResponse>>(
            $"/api/inventory/movements?productId={product.ProductId}&pageSize=2&page=2", TestData.Json);
        var byReference = await staff.GetFromJsonAsync<PagedResult<InventoryMovementResponse>>(
            $"/api/inventory/movements?referenceId={Uri.EscapeDataString($"S1/{prefix}-2")}", TestData.Json);

        Assert.Equal(3, firstPage!.TotalCount);
        Assert.Equal(new[] { 3, 2 }, firstPage.Items.Select(m => m.Stock));
        Assert.Equal(new[] { 1 }, secondPage!.Items.Select(m => m.Stock));

        var movement = Assert.Single(byReference!.Items);
        Assert.Equal((product.ProductId, product.Sku, InventoryMovementType.Import, InventoryReferenceType.StockImport),
            (movement.ProductId, movement.Sku, movement.Type, movement.ReferenceType));
        Assert.Equal(($"{prefix}-2", "S1", 2), (movement.DocumentNumber, movement.DocumentSeries, movement.Stock));
        Assert.NotNull(movement.CreatedBy);
    }

    [Fact]
    public async Task Movements_reject_sorting()
    {
        var response = await _manager.GetAsync("/api/inventory/movements?sortBy=createdAt");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Staff_cannot_import()
    {
        var product = await CreateProductAsync();
        var staff = factory.CreateClientAs(AppRoles.Staff);

        var response = await staff.PostAsJsonAsync(
            "/api/inventory/import",
            Import($"GRN-{TestData.UniqueSuffix()}", null, (product.ProductId, 1)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_import_request_is_rejected()
    {
        var product = await CreateProductAsync();

        var response = await _manager.PostAsJsonAsync(
            "/api/inventory/import",
            Import("", null, (product.ProductId, 1), (product.ProductId, 2)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ReadErrorCodeAsync());
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(5, true)] // boundary is inclusive
    public async Task Low_stock_flag_follows_imported_stock(int imported, bool expectedLow)
    {
        var product = await CreateProductAsync(lowStockThreshold: 5);
        var response = await _manager.PostAsJsonAsync(
            "/api/inventory/import",
            Import($"GRN-{TestData.UniqueSuffix()}", null, (product.ProductId, imported)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var all = await _manager.GetFromJsonAsync<PagedResult<InventoryItemResponse>>(
            $"/api/inventory?search={product.Sku}", TestData.Json);
        var lowOnly = await _manager.GetFromJsonAsync<PagedResult<InventoryItemResponse>>(
            $"/api/inventory?search={product.Sku}&lowStockOnly=true", TestData.Json);

        Assert.Equal(expectedLow, Assert.Single(all!.Items).IsLowStock);
        Assert.Equal(expectedLow ? 1 : 0, lowOnly!.TotalCount);
    }

    private async Task<ProductResponse> CreateProductAsync(int lowStockThreshold = 0)
    {
        var sku = $"INV-{TestData.UniqueSuffix()}".ToUpperInvariant();
        var response = await _manager.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest(sku, $"Product {sku}", 1m, lowStockThreshold, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<ProductResponse>();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToArray();
    }

    private Task<(int TotalStock, int Movements, int OutboxEvents)> CountsAsync(Guid productId) =>
        factory.QueryDbAsync(async db => (
            (await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId)).TotalStock,
            await db.InventoryMovements.CountAsync(m => m.ProductId == productId),
            await db.OutboxEvents.CountAsync(e => e.AggregateId == productId.ToString())));

    private static ImportInventoryRequest Import(string documentNumber, string? documentSeries, params (Guid ProductId, int Stock)[] items) =>
        new(documentNumber, documentSeries, items.Select(i => new ImportInventoryItem(i.ProductId, i.Stock)).ToList());
}
