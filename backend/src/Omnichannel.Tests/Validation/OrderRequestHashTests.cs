using Omnichannel.Application.Orders;

namespace Omnichannel.Tests.Validation;

public sealed class OrderRequestHashTests
{
    private static readonly Guid Customer = new("0199a1b0-0000-7000-8000-00000000c001");
    private static readonly Guid ProductA = new("0199a1b0-0000-7000-8000-00000000a001");
    private static readonly Guid ProductB = new("0199a1b0-0000-7000-8000-00000000b001");

    [Fact]
    public void Is_lower_case_sha256_hex_of_the_canonical_form()
    {
        var hash = OrderRequestHash.Compute(Customer, [new(ProductB, 2), new(ProductA, 1)]);

        // SHA-256 of "0199a1b0-0000-7000-8000-00000000c001|0199a1b0-...a001:1,0199a1b0-...b001:2"
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{Customer:D}|{ProductA:D}:1,{ProductB:D}:2")));
        Assert.Equal(expected, hash);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Item_order_does_not_matter()
    {
        Assert.Equal(
            OrderRequestHash.Compute(Customer, [new(ProductA, 1), new(ProductB, 2)]),
            OrderRequestHash.Compute(Customer, [new(ProductB, 2), new(ProductA, 1)]));
    }

    [Fact]
    public void Customer_product_and_quantity_change_the_hash()
    {
        var baseline = OrderRequestHash.Compute(Customer, [new(ProductA, 1)]);

        Assert.NotEqual(baseline, OrderRequestHash.Compute(Guid.NewGuid(), [new(ProductA, 1)]));
        Assert.NotEqual(baseline, OrderRequestHash.Compute(Customer, [new(ProductB, 1)]));
        Assert.NotEqual(baseline, OrderRequestHash.Compute(Customer, [new(ProductA, 2)]));
        Assert.NotEqual(baseline, OrderRequestHash.Compute(Customer, [new(ProductA, 1), new(ProductB, 1)]));
    }
}
