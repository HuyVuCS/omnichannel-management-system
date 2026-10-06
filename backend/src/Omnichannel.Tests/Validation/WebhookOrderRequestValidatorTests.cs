using Omnichannel.Application.Channels;
using Omnichannel.Application.Orders;

namespace Omnichannel.Tests.Validation;

/// <summary>Task 023: the body of a webhook order - the intake's limits in the channel's words.</summary>
public sealed class WebhookOrderRequestValidatorTests
{
    private static readonly WebhookCustomer Customer = new("C-1001", "Nguyen Van A", "+84 90 123-4567", "a@example.com");

    private readonly WebhookOrderRequestValidator _validator = new();

    private static WebhookOrderRequest Order(params WebhookOrderItem[] items) => new("WEB-1", Customer, items);

    private string[] KeysOf(WebhookOrderRequest request) =>
        _validator.Validate(request).Errors.Select(e => e.PropertyName).Distinct().Order().ToArray();

    [Fact]
    public void An_order_with_items_named_by_product_id_or_by_sku_is_valid()
    {
        var request = Order(new("WEB-100", null, 1), new(null, "SKU-A", 10_000), new("  ", " SKU-B ", 2), new("WEB-200", "", 3));

        Assert.Empty(KeysOf(request));
        // Phone and e-mail are optional; blank counts as not given.
        Assert.Empty(KeysOf(request with { Customer = new("C-1", "A", null, null) }));
        Assert.Empty(KeysOf(request with { Customer = new("C-1", "A", " ", "") }));
    }

    [Fact]
    public void The_longest_ids_are_measured_after_trimming()
    {
        var longest = new string('x', 128);
        var padded = $"  {longest}  ";

        Assert.Empty(KeysOf(new WebhookOrderRequest(padded, Customer with { ExternalCustomerId = padded }, [new(padded, null, 1), new(null, padded, 1)])));
        Assert.Equal(
            ["customer.externalCustomerId", "externalOrderId", "items[0].externalProductId", "items[1].externalSku"],
            KeysOf(new WebhookOrderRequest(longest + "x", Customer with { ExternalCustomerId = longest + "x" }, [new(longest + "x", null, 1), new(null, longest + "x", 1)])));
    }

    [Fact]
    public void Missing_parts_are_reported_under_their_json_paths()
    {
        var request = new WebhookOrderRequest(null!, null!, null!);

        Assert.Equal(["customer", "externalOrderId", "items"], KeysOf(request));
        Assert.Equal(["externalOrderId"], KeysOf(Order(new WebhookOrderItem("P", null, 1)) with { ExternalOrderId = "   " }));
        Assert.Equal(["items"], KeysOf(Order()));
    }

    [Theory]
    [InlineData(null, "A", null, null, "customer.externalCustomerId")]
    [InlineData("  ", "A", null, null, "customer.externalCustomerId")]
    [InlineData("C-1", "", null, null, "customer.name")]
    [InlineData("C-1", null, null, null, "customer.name")]
    [InlineData("C-1", "A", "090x123", null, "customer.phone")]
    [InlineData("C-1", "A", "123456789012345678901234567890123", null, "customer.phone")]
    [InlineData("C-1", "A", null, "not-an-email", "customer.email")]
    public void The_customer_follows_the_rules_of_the_customer_form(string? externalId, string? name, string? phone, string? email, string key)
    {
        var request = Order(new WebhookOrderItem("P", null, 1)) with { Customer = new WebhookCustomer(externalId!, name!, phone, email) };

        Assert.Equal([key], KeysOf(request));
    }

    [Fact]
    public void A_customer_name_may_be_as_long_as_a_customer_s()
    {
        var item = new WebhookOrderItem("P", null, 1);

        Assert.Empty(KeysOf(Order(item) with { Customer = Customer with { Name = new string('n', 200) } }));
        Assert.Equal(["customer.name"], KeysOf(Order(item) with { Customer = Customer with { Name = new string('n', 201) } }));
    }

    [Theory]
    // Neither identifier, or both: reported on the external product id.
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData("WEB-100", "SKU-A")]
    public void A_line_names_its_product_by_exactly_one_identifier(string? externalProductId, string? externalSku)
    {
        var request = Order(new WebhookOrderItem("OK", null, 1), new WebhookOrderItem(externalProductId, externalSku, 1));

        Assert.Equal(["items[1].externalProductId"], KeysOf(request));
        var error = Assert.Single(_validator.Validate(request).Errors);
        Assert.Equal("Give exactly one of 'External product ID' and 'External SKU'.", error.ErrorMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void A_quantity_is_between_one_and_ten_thousand(int quantity)
    {
        Assert.Equal(["items[0].quantity"], KeysOf(Order(new WebhookOrderItem(null, "SKU-A", quantity))));
        Assert.Empty(KeysOf(Order(new WebhookOrderItem(null, "SKU-A", 1), new WebhookOrderItem(null, "SKU-B", 10_000))));
    }

    [Fact]
    public void An_order_has_at_most_a_hundred_lines_and_none_of_them_is_null()
    {
        var hundred = Enumerable.Range(0, 100).Select(n => new WebhookOrderItem($"P-{n}", null, 1)).ToArray();

        Assert.Empty(KeysOf(Order(hundred)));
        Assert.Equal(["items"], KeysOf(Order([.. hundred, new WebhookOrderItem("P-100", null, 1)])));
        Assert.Equal(["items"], KeysOf(Order(new WebhookOrderItem("P", null, 1), null!)));
    }

    [Fact]
    public void No_product_is_named_twice()
    {
        // The same key twice - also when written with other spaces.
        Assert.Equal(["items"], KeysOf(Order(new("WEB-100", null, 1), new(" WEB-100 ", null, 2))));
        Assert.Equal(["items"], KeysOf(Order(new(null, "SKU-A", 1), new("OTHER", null, 1), new("", "SKU-A", 1))));

        // An id and a SKU with the same text are two keys (two mappings), and so are two cases of one text.
        Assert.Empty(KeysOf(Order(new("X-1", null, 1), new(null, "X-1", 1))));
        Assert.Empty(KeysOf(Order(new("web-100", null, 1), new("WEB-100", null, 1))));
        // A line that names no product has its own error and is not counted as a duplicate of another such line.
        Assert.Equal(
            ["items[0].externalProductId", "items[1].externalProductId"],
            KeysOf(Order(new(null, null, 1), new(null, null, 1))));
    }

    [Fact]
    public void A_line_s_key_is_the_key_of_the_mapping_it_names()
    {
        // One rule for how a channel knows a product: stock sync, reconciliation and webhooks agree by construction.
        Assert.Equal(ChannelProductKey.Of("WEB-100", null), new WebhookOrderItem(" WEB-100 ", null, 1).Key());
        Assert.Equal(ChannelProductKey.Of(null, "SKU-A"), new WebhookOrderItem("  ", "SKU-A ", 1).Key());
        Assert.Equal(("id:WEB-100", "sku:SKU-A"), (ChannelProductKey.Of("WEB-100", "SKU-A"), ChannelProductKey.Of(null, "SKU-A")));
        Assert.Throws<ArgumentException>(() => ChannelProductKey.Of(null, null));

        Assert.True(new WebhookOrderItem("P", null, 1).NamesOneProduct());
        Assert.True(new WebhookOrderItem(null, "S", 1).NamesOneProduct());
        Assert.False(new WebhookOrderItem("P", "S", 1).NamesOneProduct());
        Assert.False(new WebhookOrderItem(" ", null, 1).NamesOneProduct());
        Assert.Null(WebhookOrderItem.Given("   "));
        Assert.Equal("P", WebhookOrderItem.Given(" P "));
    }
}
