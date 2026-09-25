using Omnichannel.Domain.Entities;

namespace Omnichannel.Tests.Domain;

public sealed class InventoryTests
{
    [Fact]
    public void New_inventory_has_nothing_reserved()
    {
        var inventory = new Inventory(Guid.NewGuid(), totalStock: 10);

        Assert.Equal(10, inventory.TotalStock);
        Assert.Equal(0, inventory.ReservedStock);
        Assert.Equal(10, inventory.AvailableStock);
    }

    [Fact]
    public void Rejects_negative_stock()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Inventory(Guid.NewGuid(), totalStock: -1));
    }

    [Fact]
    public void Requires_product_id()
    {
        Assert.Throws<ArgumentException>(() => new Inventory(Guid.Empty, totalStock: 1));
    }
}
