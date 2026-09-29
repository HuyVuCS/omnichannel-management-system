using Omnichannel.Application.Orders;

namespace Omnichannel.Tests.Validation;

public sealed class SubmitOrderRequestValidatorTests
{
    private readonly SubmitOrderRequestValidator _validator = new();

    [Fact]
    public void Accepts_valid_order()
    {
        Assert.True(_validator.Validate(Request("EXT-1", Item(1), Item(10_000))).IsValid);
    }

    [Fact]
    public void External_order_id_length_is_checked_after_trimming()
    {
        var maximum = new string('x', SubmitOrderRequestValidator.MaxExternalOrderIdLength);

        Assert.True(_validator.Validate(Request($"  {maximum}  ", Item(1))).IsValid);
        Assert.False(_validator.Validate(Request(maximum + "x", Item(1))).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_missing_external_order_id(string? externalOrderId)
    {
        var result = _validator.Validate(Request(externalOrderId!, Item(1)));

        Assert.Contains(result.Errors, e => e.PropertyName == "externalOrderId");
    }

    [Fact]
    public void Rejects_empty_customer()
    {
        var result = _validator.Validate(new SubmitOrderRequest("EXT-1", Guid.Empty, [Item(1)]));

        Assert.Contains(result.Errors, e => e.PropertyName == "customerId");
    }

    [Fact]
    public void Rejects_empty_duplicate_and_oversized_item_lists()
    {
        var productId = Guid.NewGuid();
        var tooMany = Enumerable.Range(0, SubmitOrderRequestValidator.MaxItems + 1).Select(_ => Item(1)).ToArray();

        Assert.Contains(_validator.Validate(Request("EXT-1")).Errors, e => e.PropertyName == "items");
        Assert.Contains(
            _validator.Validate(Request("EXT-1", new SubmitOrderItem(productId, 1), new SubmitOrderItem(productId, 1))).Errors,
            e => e.PropertyName == "items");
        Assert.Contains(_validator.Validate(Request("EXT-1", tooMany)).Errors, e => e.PropertyName == "items");
        Assert.False(_validator.Validate(new SubmitOrderRequest("EXT-1", Guid.NewGuid(), [null!])).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Rejects_stock_out_of_range(int stock)
    {
        Assert.Contains(_validator.Validate(Request("EXT-1", Item(stock))).Errors, e => e.PropertyName == "items[0].stock");
    }

    [Fact]
    public void Rejects_empty_product_id()
    {
        var result = _validator.Validate(Request("EXT-1", new SubmitOrderItem(Guid.Empty, 1)));

        Assert.Contains(result.Errors, e => e.PropertyName == "items[0].productId");
    }

    private static SubmitOrderItem Item(int stock) => new(Guid.NewGuid(), stock);

    private static SubmitOrderRequest Request(string externalOrderId, params SubmitOrderItem[] items) =>
        new(externalOrderId, Guid.NewGuid(), items);
}
