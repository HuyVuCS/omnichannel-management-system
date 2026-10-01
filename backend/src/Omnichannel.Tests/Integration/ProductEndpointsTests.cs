using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Products;

namespace Omnichannel.Tests.Integration;

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProductEndpointsTests(ApiFactory factory)
{
    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);

    [Fact]
    public async Task Manager_creates_product_with_empty_inventory_row()
    {
        var sku = $"P-{TestData.UniqueSuffix()}";

        var response = await _manager.PostAsJsonAsync("/api/products", NewProduct(sku));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = await response.ReadAsync<ProductResponse>();
        Assert.Equal(sku.ToUpperInvariant(), product.Sku);
        Assert.Equal($"/api/products/{product.ProductId}", response.Headers.Location?.AbsolutePath, ignoreCase: true);
        Assert.Equal((0, 0, 0), (product.TotalStock, product.ReservedStock, product.AvailableStock));

        var inventory = await factory.QueryDbAsync(db => db.Inventories.AsNoTracking()
            .SingleAsync(i => i.ProductId == product.ProductId));
        Assert.Equal((0, 0, 0), (inventory.TotalStock, inventory.ReservedStock, inventory.AvailableStock));
    }

    [Fact]
    public async Task Duplicate_sku_differing_only_in_case_is_a_conflict()
    {
        var sku = $"p-{TestData.UniqueSuffix()}";
        var first = await _manager.PostAsJsonAsync("/api/products", NewProduct(sku.ToLowerInvariant()));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _manager.PostAsJsonAsync("/api/products", NewProduct(sku.ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ProductErrorCodes.SkuExists, await second.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Concurrent_creates_with_same_sku_produce_exactly_one_product()
    {
        var sku = $"P-{TestData.UniqueSuffix()}".ToUpperInvariant();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => _manager.PostAsJsonAsync("/api/products", NewProduct(sku))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Equal(ProductErrorCodes.SkuExists, await conflict.ReadErrorCodeAsync());
        }

        var rows = await factory.QueryDbAsync(db => db.Products.CountAsync(p => p.Sku == sku));
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Staff_cannot_create_products()
    {
        var staff = factory.CreateClientAs(AppRoles.Staff);

        var response = await staff.PostAsJsonAsync("/api/products", NewProduct($"P-{TestData.UniqueSuffix()}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_cannot_list_products()
    {
        var response = await factory.CreateClient().GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Out_of_stock_products_are_low_stock_for_any_threshold_including_zero()
    {
        var marker = TestData.UniqueSuffix();
        var thresholdFive = await CreateAsync(NewProduct($"LOW5-{marker}", lowStockThreshold: 5));
        var thresholdZero = await CreateAsync(NewProduct($"LOW0-{marker}", lowStockThreshold: 0));

        var response = await _manager.GetAsync($"/api/inventory?lowStockOnly=true&search={marker}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.ReadAsync<PagedResult<InventoryItemResponse>>();
        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, i => Assert.True(i.IsLowStock));
        Assert.Equal(
            new[] { thresholdFive.ProductId, thresholdZero.ProductId }.Order(),
            page.Items.Select(i => i.ProductId).Order());
    }

    [Fact]
    public async Task Product_responses_carry_the_low_stock_flag_of_the_shared_rule()
    {
        var marker = TestData.UniqueSuffix().ToUpperInvariant();
        var created = await CreateAsync(NewProduct($"FLAG-{marker}", lowStockThreshold: 5));

        async Task<(bool Detail, bool List, bool Inventory, int Available)> FlagsAsync()
        {
            var detail = await (await _manager.GetAsync($"/api/products/{created.ProductId}")).ReadAsync<ProductResponse>();
            var list = await (await _manager.GetAsync($"/api/products?search=FLAG-{marker}")).ReadAsync<PagedResult<ProductResponse>>();
            var inventory = await (await _manager.GetAsync($"/api/inventory?search=FLAG-{marker}")).ReadAsync<PagedResult<InventoryItemResponse>>();
            return (detail.IsLowStock, Assert.Single(list.Items).IsLowStock, Assert.Single(inventory.Items).IsLowStock, detail.AvailableStock);
        }

        async Task ImportAsync(int stock)
        {
            var response = await _manager.PostAsJsonAsync(
                "/api/inventory/import",
                new ImportInventoryRequest($"GRN-{TestData.UniqueSuffix()}", null, [new ImportInventoryItem(created.ProductId, stock)]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Out of stock, threshold 5: low - already in the create response.
        Assert.True(created.IsLowStock);
        Assert.Equal((true, true, true, 0), await FlagsAsync());

        // Exactly at the threshold is still low (inclusive rule) ...
        await ImportAsync(5);
        Assert.Equal((true, true, true, 5), await FlagsAsync());

        // ... one above it is not. Product list, product detail and inventory list always agree.
        await ImportAsync(1);
        Assert.Equal((false, false, false, 6), await FlagsAsync());

        // Raising the threshold makes it low again without any stock change.
        var updated = await _manager.PutAsJsonAsync(
            $"/api/products/{created.ProductId}",
            new UpdateProductRequest(created.Name, created.Price, 6, null));
        Assert.True((await updated.ReadAsync<ProductResponse>()).IsLowStock);
        Assert.Equal((true, true, true, 6), await FlagsAsync());
    }

    [Fact]
    public async Task Sku_is_normalised_to_upper_case()
    {
        var suffix = TestData.UniqueSuffix();

        var created = await CreateAsync(NewProduct($"abc-1.x_2-{suffix}"));

        Assert.Equal($"ABC-1.X_2-{suffix}".ToUpperInvariant(), created.Sku);
    }

    [Theory]
    [InlineData("AB C")]
    [InlineData("A/B")]
    [InlineData("-AB")]
    [InlineData("ÁB")]
    public async Task Sku_with_disallowed_characters_is_rejected(string sku)
    {
        var response = await _manager.PostAsJsonAsync("/api/products", NewProduct(sku));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Product_list_is_searchable_and_includes_available_stock()
    {
        var marker = TestData.UniqueSuffix();
        var created = await CreateAsync(NewProduct($"S-{marker}"));

        var page = await _manager.GetFromJsonAsync<PagedResult<ProductResponse>>(
            $"/api/products?search={marker}&sortBy=name&sortDir=desc", TestData.Json);

        var item = Assert.Single(page!.Items);
        Assert.Equal(created.ProductId, item.ProductId);
        Assert.Equal(0, item.AvailableStock);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Update_changes_details_but_not_sku()
    {
        var created = await CreateAsync(NewProduct($"U-{TestData.UniqueSuffix()}"));

        var response = await _manager.PutAsJsonAsync(
            $"/api/products/{created.ProductId}",
            new UpdateProductRequest("Renamed", 12.5m, 3, new DateOnly(2027, 1, 31)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.ReadAsync<ProductResponse>();
        Assert.Equal(created.Sku, updated.Sku);
        Assert.Equal(("Renamed", 12.5m, 3), (updated.Name, updated.Price, updated.LowStockThreshold));
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task Unknown_product_is_not_found()
    {
        var response = await _manager.GetAsync($"/api/products/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await response.ReadErrorCodeAsync());
    }

    private async Task<ProductResponse> CreateAsync(CreateProductRequest request)
    {
        var response = await _manager.PostAsJsonAsync("/api/products", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<ProductResponse>();
    }

    private static CreateProductRequest NewProduct(string sku, int lowStockThreshold = 1) =>
        new(sku, $"Product {sku}", 9.99m, lowStockThreshold, ExpiryDate: null);
}
