using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Domain;

public sealed class OrderTests
{
    private static Order NewOrder() => new(Guid.NewGuid(), Guid.NewGuid(), " EXT-1001 ", OrderStatus.Pending);

    [Fact]
    public void Trims_external_order_id()
    {
        Assert.Equal("EXT-1001", NewOrder().ExternalOrderId);
    }

    [Fact]
    public void AddItem_accumulates_total_amount()
    {
        var order = NewOrder();

        order.AddItem(Guid.NewGuid(), stock: 2, unitPrice: 10.50m);
        order.AddItem(Guid.NewGuid(), stock: 1, unitPrice: 4m);

        Assert.Equal(25m, order.TotalAmount);
        Assert.Equal(2, order.Items.Count);
        Assert.All(order.Items, item => Assert.Equal(order.OrderId, item.OrderId));
    }

    [Fact]
    public void AddItem_rejects_the_same_product_twice()
    {
        var order = NewOrder();
        var productId = Guid.NewGuid();
        order.AddItem(productId, 1, 1m);

        Assert.Throws<InvalidOperationException>(() => order.AddItem(productId, 1, 1m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void AddItem_rejects_non_positive_quantity(int stock)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewOrder().AddItem(Guid.NewGuid(), stock, 1m));
    }
}
