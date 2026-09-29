using Omnichannel.Application.Customers;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Products;

namespace Omnichannel.Tests.Validation;

public sealed class PageQueryValidatorTests
{
    private readonly ProductListQueryValidator _products = new();

    [Fact]
    public void Defaults_are_valid()
    {
        var query = new ProductListQuery();

        Assert.True(_products.Validate(query).IsValid);
        Assert.Equal((1, 20, 0), (query.Page, query.PageSize, query.Skip));
        Assert.False(query.IsDescending);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(10_001, 20)]
    [InlineData(int.MaxValue, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void Rejects_out_of_range_page_or_size(int page, int pageSize)
    {
        Assert.False(_products.Validate(new ProductListQuery { Page = page, PageSize = pageSize }).IsValid);
    }

    [Fact]
    public void Accepts_maximum_page_size()
    {
        Assert.True(_products.Validate(new ProductListQuery { PageSize = 100 }).IsValid);
    }

    [Fact]
    public void Rejects_oversized_search()
    {
        Assert.False(_products.Validate(new ProductListQuery { Search = new string('a', 101) }).IsValid);
    }

    [Theory]
    [InlineData("sku", "asc")]
    [InlineData("Price", "DESC")]
    [InlineData("createdAt", null)]
    public void Accepts_whitelisted_sort_case_insensitively(string sortBy, string? sortDir)
    {
        Assert.True(_products.Validate(new ProductListQuery { SortBy = sortBy, SortDir = sortDir }).IsValid);
    }

    [Theory]
    [InlineData("unknown", null)]
    [InlineData("availableStock", null)] // valid for inventory, not for products
    [InlineData("name", "sideways")]
    public void Rejects_unknown_sort(string sortBy, string? sortDir)
    {
        Assert.False(_products.Validate(new ProductListQuery { SortBy = sortBy, SortDir = sortDir }).IsValid);
    }

    [Fact]
    public void Each_endpoint_has_its_own_sort_whitelist()
    {
        Assert.True(new InventoryListQueryValidator().Validate(new InventoryListQuery { SortBy = "availableStock" }).IsValid);
        Assert.True(new CustomerListQueryValidator().Validate(new CustomerListQuery { SortBy = "name" }).IsValid);
        Assert.False(new CustomerListQueryValidator().Validate(new CustomerListQuery { SortBy = "sku" }).IsValid);
    }

    [Fact]
    public void Skip_and_search_term_are_derived()
    {
        var query = new CustomerListQuery { Page = 3, PageSize = 25, Search = "  ann  ", SortDir = "desc" };

        Assert.Equal(50, query.Skip);
        Assert.Equal("ann", query.SearchTerm);
        Assert.True(query.IsDescending);
        Assert.Null(new CustomerListQuery { Search = "   " }.SearchTerm);
    }
}
