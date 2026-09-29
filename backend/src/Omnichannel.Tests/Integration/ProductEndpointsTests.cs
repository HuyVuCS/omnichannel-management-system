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
    public async Task Low_stock_filter_returns_products_at_or_below_a_positive_threshold()
    {
        var marker = TestData.UniqueSuffix();
        var low = await CreateAsync(NewProduct($"LOW-{marker}", lowStockThreshold: 5));
        var thresholdOff = await CreateAsync(NewProduct($"OFF-{marker}", lowStockThreshold: 0));

        var response = await _manager.GetAsync($"/api/inventory?lowStockOnly=true&search={marker}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.ReadAsync<PagedResult<InventoryItemResponse>>();
        var item = Assert.Single(page.Items);
        Assert.Equal(low.ProductId, item.ProductId);
        Assert.True(item.IsLowStock);
        Assert.Equal(5, item.LowStockThreshold);
        Assert.Equal(0, item.AvailableStock);
        Assert.DoesNotContain(page.Items, i => i.ProductId == thresholdOff.ProductId);
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
