using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Domain;

/// <summary>Task 024: the rules of the payment of an order and of its invoice, in the domain.</summary>
public sealed class OrderPaymentTests
{
    private static readonly DateTimeOffset PaidAt = new(2026, 10, 9, 8, 30, 0, TimeSpan.Zero);

    private static Order NewOrder(OrderStatus status = OrderStatus.Reserved)
    {
        var order = new Order(Guid.NewGuid(), Guid.NewGuid(), "EXT-1001", status);
        order.AddItem(Guid.NewGuid(), 2, 10m);
        return order;
    }

    [Fact]
    public void A_new_order_is_unpaid_and_has_no_payment_field()
    {
        var order = NewOrder();

        Assert.Equal(PaymentStatus.Unpaid, order.PaymentStatus);
        Assert.Equal((null, null, null, null), (order.PaidAt, order.PaidBy, order.PaymentMethod, order.PaymentReference));
    }

    [Fact]
    public void Paying_records_the_payment_and_changes_neither_the_status_nor_the_total()
    {
        var order = NewOrder();

        order.MarkPaid(PaymentMethod.BankTransfer, "  FT-123  ", "user-1", PaidAt);

        Assert.Equal(
            (PaymentStatus.Paid, (DateTimeOffset?)PaidAt, "user-1", (PaymentMethod?)PaymentMethod.BankTransfer, "FT-123"),
            (order.PaymentStatus, order.PaidAt, order.PaidBy, order.PaymentMethod, order.PaymentReference));
        Assert.Equal((OrderStatus.Reserved, 20m), (order.Status, order.TotalAmount));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_reference_is_no_reference(string? reference)
    {
        var order = NewOrder();

        order.MarkPaid(PaymentMethod.Cash, reference, "user-1", PaidAt);

        Assert.Null(order.PaymentReference);
        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.InsufficientStock)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Completed)]
    public void Only_a_reserved_order_can_be_paid(OrderStatus status)
    {
        var order = NewOrder(status);

        Assert.Throws<InvalidOperationException>(() => order.MarkPaid(PaymentMethod.Cash, null, "user-1", PaidAt));

        Assert.Equal((PaymentStatus.Unpaid, (DateTimeOffset?)null), (order.PaymentStatus, order.PaidAt));
    }

    [Fact]
    public void An_order_is_paid_once_and_the_first_payment_stays()
    {
        var order = NewOrder();
        order.MarkPaid(PaymentMethod.Cash, "R-1", "user-1", PaidAt);

        Assert.Throws<InvalidOperationException>(() => order.MarkPaid(PaymentMethod.Card, "R-2", "user-2", PaidAt.AddHours(1)));

        Assert.Equal(
            ((DateTimeOffset?)PaidAt, "user-1", (PaymentMethod?)PaymentMethod.Cash, "R-1"),
            (order.PaidAt, order.PaidBy, order.PaymentMethod, order.PaymentReference));
    }

    [Fact]
    public void A_payment_needs_who_recorded_it_a_known_method_and_a_reference_that_fits()
    {
        Assert.ThrowsAny<ArgumentException>(() => NewOrder().MarkPaid(PaymentMethod.Cash, null, " ", PaidAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewOrder().MarkPaid((PaymentMethod)99, null, "user-1", PaidAt));
        Assert.Throws<ArgumentException>(() => NewOrder().MarkPaid(
            PaymentMethod.Cash, new string('x', Order.MaxPaymentReferenceLength + 1), "user-1", PaidAt));

        // The limit is measured as the reference is stored: trimmed.
        var order = NewOrder();
        order.MarkPaid(PaymentMethod.Cash, $" {new string('x', Order.MaxPaymentReferenceLength)} ", "user-1", PaidAt);
        Assert.Equal(Order.MaxPaymentReferenceLength, order.PaymentReference!.Length);
    }

    [Fact]
    public void A_refused_payment_leaves_the_order_unpaid()
    {
        var order = NewOrder();

        Assert.ThrowsAny<ArgumentException>(() => order.MarkPaid(PaymentMethod.Cash, null, "", PaidAt));

        Assert.Equal(PaymentStatus.Unpaid, order.PaymentStatus);
        Assert.Equal((null, null, null, null), (order.PaidAt, order.PaidBy, order.PaymentMethod, order.PaymentReference));
    }

    // ---- the keys of what a payment starts ---------------------------------------------------------------------

    [Fact]
    public void The_two_tasks_of_a_payment_have_one_key_each_per_order()
    {
        var orderId = Guid.Parse("0199c1f0-0000-7000-8000-000000000001");

        Assert.Equal("invoice:0199c1f0-0000-7000-8000-000000000001", PaymentTaskKeys.ForInvoice(orderId));
        Assert.Equal("email:payment:0199c1f0-0000-7000-8000-000000000001", PaymentTaskKeys.ForEmail(orderId));
        // The invoice task and the call to the provider share the key.
        Assert.Equal(Invoice.ProviderKeyOf(orderId), PaymentTaskKeys.ForInvoice(orderId));
        Assert.NotEqual(PaymentTaskKeys.ForInvoice(orderId), PaymentTaskKeys.ForInvoice(Guid.NewGuid()));
        Assert.True(PaymentTaskKeys.ForEmail(orderId).Length <= ProcessingTask.MaxIdempotencyKeyLength);
        Assert.True(Invoice.ProviderKeyOf(orderId).Length <= Invoice.MaxProviderReferenceLength);
    }

    // ---- the invoice -------------------------------------------------------------------------------------------

    [Fact]
    public void An_invoice_keeps_what_the_provider_issued_for_its_order()
    {
        var orderId = Guid.NewGuid();

        var invoice = new Invoice(orderId, "SIM-202610-0A1B2C3D", PaidAt, 150_000m, Invoice.ProviderKeyOf(orderId));

        Assert.Equal(
            (orderId, "SIM-202610-0A1B2C3D", PaidAt, 150_000m, $"invoice:{orderId}"),
            (invoice.OrderId, invoice.InvoiceNumber, invoice.IssuedAt, invoice.TotalAmount, invoice.ProviderReference));
    }

    [Fact]
    public void An_invoice_needs_an_order_a_number_a_key_and_a_total_that_is_not_negative()
    {
        var orderId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new Invoice(Guid.Empty, "N-1", PaidAt, 1m, "key"));
        Assert.ThrowsAny<ArgumentException>(() => new Invoice(orderId, " ", PaidAt, 1m, "key"));
        Assert.ThrowsAny<ArgumentException>(() => new Invoice(orderId, "N-1", PaidAt, 1m, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Invoice(orderId, "N-1", PaidAt, -0.01m, "key"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Invoice(orderId, new string('9', Invoice.MaxInvoiceNumberLength + 1), PaidAt, 1m, "key"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Invoice(orderId, "N-1", PaidAt, 1m, new string('k', Invoice.MaxProviderReferenceLength + 1)));

        // An order without an amount still gets its invoice.
        Assert.Equal(0m, new Invoice(orderId, new string('9', Invoice.MaxInvoiceNumberLength), PaidAt, 0m, "key").TotalAmount);
    }
}
