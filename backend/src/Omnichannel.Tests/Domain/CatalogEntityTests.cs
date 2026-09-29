using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Domain;

public sealed class ProductTests
{
    [Fact]
    public void Sku_is_normalised_and_immutable_through_update()
    {
        var product = new Product("  abc-1 ", "Milk", 10m, 2, null);

        product.Update(" Fresh milk ", 11m, 3, new DateOnly(2027, 1, 1));

        Assert.Equal("ABC-1", product.Sku);
        Assert.Equal(("Fresh milk", 11m, 3), (product.Name, product.Price, product.LowStockThreshold));
    }

    [Fact]
    public void Sku_format_is_guarded_by_the_entity()
    {
        Assert.Equal("ABC-1.X_2", new Product("abc-1.x_2", "Milk", 1m, 0, null).Sku);

        Assert.Throws<ArgumentException>(() => new Product("AB C", "Milk", 1m, 0, null));
        Assert.Throws<ArgumentException>(() => new Product("-AB", "Milk", 1m, 0, null));
        Assert.Throws<ArgumentException>(() => new Product("ÁB", "Milk", 1m, 0, null));
        Assert.Throws<ArgumentException>(() => new Product(new string('A', Product.MaxSkuLength + 1), "Milk", 1m, 0, null));
    }

    [Fact]
    public void Update_rejects_invalid_values()
    {
        var product = new Product("SKU", "Milk", 10m, 2, null);

        Assert.Throws<ArgumentException>(() => product.Update(" ", 1m, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => product.Update("Milk", -1m, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => product.Update("Milk", 1m, -1, null));
        Assert.Equal("Milk", product.Name);
    }
}

public sealed class ChannelTests
{
    [Fact]
    public void Update_changes_name_and_status_only()
    {
        var channel = new Channel("Web", "web", ChannelType.Website);

        channel.Update(" Website ", ChannelStatus.Inactive);

        Assert.Equal(("Website", ChannelStatus.Inactive, "web", ChannelType.Website),
            (channel.Name, channel.Status, channel.Slug, channel.Type));
    }

    [Fact]
    public void Update_rejects_blank_name_and_unknown_status()
    {
        var channel = new Channel("Web", "web", ChannelType.Website);

        Assert.Throws<ArgumentException>(() => channel.Update("", ChannelStatus.Active));
        Assert.Throws<ArgumentOutOfRangeException>(() => channel.Update("Web", (ChannelStatus)99));
    }
}

public sealed class CustomerTests
{
    [Fact]
    public void Blank_optional_fields_are_stored_as_null()
    {
        var customer = new Customer(" An ", " ", "", null);

        Assert.Equal("An", customer.Name);
        Assert.Null(customer.Phone);
        Assert.Null(customer.Email);
        Assert.Null(customer.Address);
    }

    [Fact]
    public void Update_trims_values_and_rejects_blank_name()
    {
        var customer = new Customer("An", null, null, null);

        customer.Update("Binh", " 0901 ", " b@example.com ", " HCMC ");

        Assert.Equal(("Binh", "0901", "b@example.com", "HCMC"), (customer.Name, customer.Phone, customer.Email, customer.Address));
        Assert.Throws<ArgumentException>(() => customer.Update("  ", null, null, null));
    }
}
