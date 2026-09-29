using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Customers;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Validation;

public sealed class ProductRequestValidatorTests
{
    private readonly CreateProductRequestValidator _create = new();
    private readonly UpdateProductRequestValidator _update = new();

    [Fact]
    public void Accepts_valid_product()
    {
        Assert.True(_create.Validate(new CreateProductRequest("SKU-1", "Milk", 12.50m, 5, null)).IsValid);
        Assert.True(_update.Validate(new UpdateProductRequest("Milk", 0m, 0, new DateOnly(2027, 1, 1))).IsValid);
    }

    [Theory]
    [InlineData("", "Milk", 1, 0, nameof(CreateProductRequest.Sku))]
    [InlineData("SKU", " ", 1, 0, nameof(CreateProductRequest.Name))]
    [InlineData("SKU", "Milk", -0.01, 0, nameof(CreateProductRequest.Price))]
    [InlineData("SKU", "Milk", 1.001, 0, nameof(CreateProductRequest.Price))]
    [InlineData("SKU", "Milk", 1, -1, nameof(CreateProductRequest.LowStockThreshold))]
    public void Rejects_invalid_product(string sku, string name, double price, int threshold, string property)
    {
        var result = _create.Validate(new CreateProductRequest(sku, name, (decimal)price, threshold, null));

        Assert.Contains(result.Errors, e => e.PropertyName == ValidationConventions.JsonName(property));
    }

    [Fact]
    public void Rejects_oversized_sku()
    {
        var sku = new string('A', CreateProductRequestValidator.MaxSkuLength + 1);

        Assert.False(_create.Validate(new CreateProductRequest(sku, "Milk", 1m, 0, null)).IsValid);
    }

    [Theory]
    [InlineData("abc-1.x_2")]
    [InlineData(" 9A ")]
    [InlineData("A")]
    public void Accepts_sku_format_case_insensitively(string sku)
    {
        Assert.True(_create.Validate(new CreateProductRequest(sku, "Milk", 1m, 0, null)).IsValid);
    }

    [Theory]
    [InlineData("AB C")]
    [InlineData("A/B")]
    [InlineData("-AB")]
    [InlineData(".AB")]
    [InlineData("ÁB")]
    [InlineData(null)]
    public void Rejects_sku_format(string? sku)
    {
        var result = _create.Validate(new CreateProductRequest(sku!, "Milk", 1m, 0, null));

        Assert.Contains(result.Errors, e => e.PropertyName == ValidationConventions.JsonName(nameof(CreateProductRequest.Sku)));
    }
}

public sealed class ChannelRequestValidatorTests
{
    private readonly CreateChannelRequestValidator _create = new();
    private readonly UpdateChannelRequestValidator _update = new();

    [Theory]
    [InlineData("website")]
    [InlineData("digital-platform")]
    [InlineData("pos2")]
    public void Accepts_valid_slugs(string slug)
    {
        Assert.True(_create.Validate(new CreateChannelRequest("Name", slug, ChannelType.Website)).IsValid);
    }

    [Theory]
    [InlineData("Web Site")]
    [InlineData("Website")]
    [InlineData("-website")]
    [InlineData("web--site")]
    [InlineData("website-")]
    [InlineData("web_site")]
    [InlineData("")]
    public void Rejects_invalid_slugs(string slug)
    {
        var result = _create.Validate(new CreateChannelRequest("Name", slug, ChannelType.Website));

        Assert.Contains(result.Errors, e => e.PropertyName == ValidationConventions.JsonName(nameof(CreateChannelRequest.Slug)));
    }

    [Fact]
    public void Rejects_oversized_slug()
    {
        var slug = new string('a', CreateChannelRequestValidator.MaxSlugLength + 1);

        Assert.False(_create.Validate(new CreateChannelRequest("Name", slug, ChannelType.Website)).IsValid);
    }

    [Fact]
    public void Rejects_undefined_enum_values()
    {
        Assert.False(_create.Validate(new CreateChannelRequest("Name", "web", (ChannelType)99)).IsValid);
        Assert.False(_update.Validate(new UpdateChannelRequest("Name", (ChannelStatus)99)).IsValid);
    }
}

public sealed class CustomerRequestValidatorTests
{
    private readonly CustomerRequestValidator _validator = new();

    [Fact]
    public void Accepts_name_only_and_full_customer()
    {
        Assert.True(_validator.Validate(new CustomerRequest("An", null, null, null)).IsValid);
        Assert.True(_validator.Validate(new CustomerRequest("An", "+84 (28) 123-456", "an@example.com", "HCMC")).IsValid);
        Assert.True(_validator.Validate(new CustomerRequest("An", "", "", "")).IsValid);
    }

    [Theory]
    [InlineData("", null, null, nameof(CustomerRequest.Name))]
    [InlineData("An", "090x123", null, nameof(CustomerRequest.Phone))]
    [InlineData("An", "123456789012345678901234567890123", null, nameof(CustomerRequest.Phone))]
    [InlineData("An", null, "not-an-email", nameof(CustomerRequest.Email))]
    public void Rejects_invalid_customer(string name, string? phone, string? email, string property)
    {
        var result = _validator.Validate(new CustomerRequest(name, phone, email, null));

        Assert.Contains(result.Errors, e => e.PropertyName == ValidationConventions.JsonName(property));
    }
}
