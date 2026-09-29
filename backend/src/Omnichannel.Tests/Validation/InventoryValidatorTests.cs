using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Inventory;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Validation;

public sealed class ImportInventoryRequestValidatorTests
{
    private readonly ImportInventoryRequestValidator _validator = new();

    [Fact]
    public void Accepts_valid_import()
    {
        var request = Request("GRN-1", "2026", Item(1), Item(1_000_000));

        Assert.True(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void Series_is_optional()
    {
        Assert.True(_validator.Validate(Request("GRN-1", null, Item(5))).IsValid);
    }

    [Fact]
    public void Rejects_duplicate_products()
    {
        var productId = Guid.NewGuid();

        var result = _validator.Validate(Request("GRN-1", null,
            new ImportInventoryItem(productId, 1), new ImportInventoryItem(productId, 2)));

        Assert.Contains(result.Errors, e => e.PropertyName == ValidationConventions.JsonName(nameof(ImportInventoryRequest.Items)));
    }

    [Fact]
    public void Rejects_empty_and_oversized_item_lists()
    {
        Assert.False(_validator.Validate(Request("GRN-1", null)).IsValid);

        var tooMany = Enumerable.Range(0, ImportInventoryRequestValidator.MaxItems + 1).Select(_ => Item(1)).ToArray();
        Assert.False(_validator.Validate(Request("GRN-1", null, tooMany)).IsValid);

        var maximum = Enumerable.Range(0, ImportInventoryRequestValidator.MaxItems).Select(_ => Item(1)).ToArray();
        Assert.True(_validator.Validate(Request("GRN-1", null, maximum)).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void Rejects_stock_out_of_range(int stock)
    {
        var result = _validator.Validate(Request("GRN-1", null, Item(stock)));

        Assert.Contains(result.Errors, e => e.PropertyName == "items[0].stock");
    }

    [Fact]
    public void Rejects_empty_product_id()
    {
        var result = _validator.Validate(Request("GRN-1", null, new ImportInventoryItem(Guid.Empty, 1)));

        Assert.Contains(result.Errors, e => e.PropertyName == "items[0].productId");
    }

    [Fact]
    public void Rejects_null_items_without_throwing()
    {
        var result = _validator.Validate(new ImportInventoryRequest("GRN-1", null, [null!]));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345", null)] // 65 chars
    [InlineData("GRN-1", "123456789012345678901234567890123")] // 33 chars
    public void Rejects_invalid_document_fields(string documentNumber, string? documentSeries)
    {
        Assert.False(_validator.Validate(Request(documentNumber, documentSeries, Item(1))).IsValid);
    }

    private static ImportInventoryItem Item(int stock) => new(Guid.NewGuid(), stock);

    private static ImportInventoryRequest Request(string number, string? series, params ImportInventoryItem[] items) =>
        new(number, series, items);
}

public sealed class InventoryMovementListQueryValidatorTests
{
    private readonly InventoryMovementListQueryValidator _validator = new();

    [Fact]
    public void Accepts_filters()
    {
        var query = new InventoryMovementListQuery
        {
            ProductId = Guid.NewGuid(),
            Type = InventoryMovementType.Import,
            ReferenceType = InventoryReferenceType.StockImport,
            ReferenceId = "2026/GRN-1",
            From = DateTimeOffset.UtcNow.AddDays(-1),
            To = DateTimeOffset.UtcNow
        };

        Assert.True(_validator.Validate(query).IsValid);
    }

    [Fact]
    public void Rejects_inverted_range_sorting_and_unknown_enums()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(_validator.Validate(new InventoryMovementListQuery { From = now, To = now.AddSeconds(-1) }).IsValid);
        Assert.False(_validator.Validate(new InventoryMovementListQuery { SortBy = "createdAt" }).IsValid);
        Assert.False(_validator.Validate(new InventoryMovementListQuery { Type = (InventoryMovementType)99 }).IsValid);
    }
}
