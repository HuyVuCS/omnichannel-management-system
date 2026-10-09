using System.Globalization;
using System.Reflection;
using System.Text;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Notifications;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Workers.InvoiceWorker;
using Omnichannel.Workers.NotificationWorker;
using Omnichannel.Workers.Processing;

namespace Omnichannel.Tests.Notifications;

/// <summary>
/// Task 024: the text of the payment confirmation in both languages, the settings of the two workers, the body of a
/// payment request, and the parser of the messages that name a task.
/// </summary>
public sealed class PaymentConfirmationEmailTests
{
    private static readonly CultureInfo English = CultureInfo.InvariantCulture; // the default .resx
    private static readonly CultureInfo Vietnamese = new("vi");
    private static readonly DateTimeOffset PaidAt = new(2026, 10, 9, 8, 30, 0, TimeSpan.Zero);

    private static readonly PaidOrderNotice Notice = new(
        Guid.NewGuid(),
        "WEB-20261009-0001",
        "Nguyen Van A",
        "anna@example.com",
        PaymentStatus.Paid,
        PaidAt,
        211_501.5m,
        [
            new PaidOrderLine("TEA-GREEN-250", "Green tea 250 g", 2, 75_000m),
            // A name that looks like a placeholder stays what it is.
            new PaidOrderLine("TEA-OOLONG", "Oolong {CustomerName}", 3, 20_500.5m)
        ]);

    private static TimeZoneInfo HoChiMinh => TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    // ---- the text ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_confirmation_in_english()
    {
        var content = PaymentConfirmationEmail.Build(Notice, PaidAt, "en", HoChiMinh);

        Assert.Equal("Payment received for order WEB-20261009-0001", content.Subject);
        Assert.Equal(
            "Hello Nguyen Van A,\n"
            + "\n"
            + "We have received your payment for order WEB-20261009-0001.\n"
            + "\n"
            + "- TEA-GREEN-250 | Green tea 250 g | 2 x 75,000 = 150,000\n"
            + "- TEA-OOLONG | Oolong {CustomerName} | 3 x 20,500.5 = 61,501.5\n"
            + "\n"
            + "Total: 211,501.5 VND\n"
            + "Paid at: 2026-10-09 15:30 (Asia/Ho_Chi_Minh)\n"
            + "\n"
            + "Thank you for your order.",
            content.TextBody);
    }

    [Fact]
    public void The_confirmation_in_vietnamese()
    {
        var content = PaymentConfirmationEmail.Build(Notice, PaidAt, "vi", HoChiMinh);

        Assert.Equal("Đã nhận thanh toán cho đơn hàng WEB-20261009-0001", content.Subject);
        Assert.Equal(
            "Xin chào Nguyen Van A,\n"
            + "\n"
            + "Chúng tôi đã nhận được thanh toán cho đơn hàng WEB-20261009-0001.\n"
            + "\n"
            + "- TEA-GREEN-250 | Green tea 250 g | 2 x 75.000 = 150.000\n"
            + "- TEA-OOLONG | Oolong {CustomerName} | 3 x 20.500,5 = 61.501,5\n"
            + "\n"
            + "Tổng tiền: 211.501,5 VND\n"
            + "Thời điểm thanh toán: 09/10/2026 15:30 (Asia/Ho_Chi_Minh)\n"
            + "\n"
            + "Cảm ơn quý khách đã đặt hàng.",
            content.TextBody);
    }

    [Fact]
    public void The_time_is_the_time_of_the_configured_zone_and_the_text_does_not_depend_on_the_culture_of_the_thread()
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            // A culture with its own digits, calendar and separators must not leak into an e-mail.
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("ar-SA");

            var berlin = PaymentConfirmationEmail.Build(Notice, PaidAt, "en", TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
            var utc = PaymentConfirmationEmail.Build(Notice, new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.Zero), "vi", TimeZoneInfo.Utc);

            Assert.Contains("Paid at: 2026-10-09 10:30 (Europe/Berlin)", berlin.TextBody);
            Assert.Contains("Total: 211,501.5 VND", berlin.TextBody);
            Assert.Contains("Thời điểm thanh toán: 31/12/2026 23:59 (UTC)", utc.TextBody);
            // The next day in Ho Chi Minh City.
            Assert.Contains(
                "01/01/2027 06:59",
                PaymentConfirmationEmail.Build(Notice, new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.Zero), "vi", HoChiMinh).TextBody);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
        }
    }

    [Fact]
    public void A_language_without_texts_falls_back_to_vietnamese_the_default()
    {
        Assert.StartsWith("Xin chào", PaymentConfirmationEmail.Build(Notice, PaidAt, "fr", HoChiMinh).TextBody);
        Assert.Equal(("vi", "Asia/Ho_Chi_Minh"), (new NotificationsOptions().Language, new NotificationsOptions().TimeZone));
    }

    [Fact]
    public void An_order_without_lines_or_amount_still_gives_a_text()
    {
        var content = PaymentConfirmationEmail.Build(Notice with { Lines = [], TotalAmount = 0m }, PaidAt, "en", HoChiMinh);

        Assert.Contains("Total: 0 VND", content.TextBody);
        Assert.DoesNotContain("{", content.TextBody);
    }

    [Fact]
    public void Every_notification_text_exists_in_english_and_vietnamese_and_they_differ()
    {
        var keys = typeof(NotificationTexts.Keys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(4, keys.Count);
        Assert.All(keys, key =>
        {
            var english = NotificationTexts.FindExact(key, English);
            var vietnamese = NotificationTexts.FindExact(key, Vietnamese);
            Assert.False(string.IsNullOrWhiteSpace(english), $"no English text for {key}");
            Assert.False(string.IsNullOrWhiteSpace(vietnamese), $"no Vietnamese text for {key}");
        });
        // The words differ; the line of an order has no words.
        Assert.All(
            new[] { NotificationTexts.Keys.PaymentSubject, NotificationTexts.Keys.PaymentBody, NotificationTexts.Keys.DateTimeFormat },
            key => Assert.NotEqual(NotificationTexts.FindExact(key, English), NotificationTexts.FindExact(key, Vietnamese)));
    }

    [Fact]
    public void A_text_needs_every_value_it_names_and_takes_no_value_it_does_not_name()
    {
        var subject = NotificationTexts.Keys.PaymentSubject;

        Assert.Equal("Payment received for order X-1", NotificationTexts.Get(subject, English, ("ExternalOrderId", "X-1")));
        // A value is put in as it is: braces in it are not looked at again.
        Assert.Equal("Payment received for order {ExternalOrderId}{Total}", NotificationTexts.Get(subject, English, ("ExternalOrderId", "{ExternalOrderId}{Total}")));
        Assert.Throws<InvalidOperationException>(() => NotificationTexts.Get(subject, English));
        Assert.Throws<InvalidOperationException>(() => NotificationTexts.Get(subject, English, ("ExternalOrderId", "X-1"), ("Total", "1")));
        Assert.Throws<InvalidOperationException>(() => NotificationTexts.Get("NoSuchText", English));
    }

    // ---- settings ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("vi", "Asia/Ho_Chi_Minh", true)]
    [InlineData("en", "Europe/Berlin", true)]
    [InlineData("en", "UTC", true)]
    [InlineData("fr", "Asia/Ho_Chi_Minh", false)]
    [InlineData("VI", "Asia/Ho_Chi_Minh", false)]
    [InlineData("", "Asia/Ho_Chi_Minh", false)]
    [InlineData("vi", "Mars/Olympus", false)]
    [InlineData("vi", "", false)]
    public void The_notification_settings_need_a_language_with_texts_and_a_zone_the_host_knows(string language, string timeZone, bool valid)
    {
        var result = new NotificationsOptionsValidator().Validate(null, new NotificationsOptions { Language = language, TimeZone = timeZone });

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.All(result.Failures!, failure => Assert.StartsWith("Notifications:", failure));
        }
    }

    [Fact]
    public void The_defaults_of_both_workers_are_valid_one_message_at_a_time_and_five_attempts()
    {
        var invoice = new InvoiceWorkerOptions();
        var notification = new NotificationWorkerOptions();

        Assert.True(new InvoiceWorkerOptionsValidator().Validate(null, invoice).Succeeded);
        Assert.True(new NotificationWorkerOptionsValidator().Validate(null, notification).Succeeded);
        Assert.Equal(((ushort)1, 5, TimeSpan.FromSeconds(10)), (invoice.Concurrency, invoice.MaxAttempts, invoice.CallTimeout));
        Assert.Equal(((ushort)1, 5, TimeSpan.FromSeconds(10)), (notification.Concurrency, notification.MaxAttempts, notification.CallTimeout));
        Assert.Equal(("InvoiceWorker", "NotificationWorker"), (InvoiceWorkerOptions.SectionName, NotificationWorkerOptions.SectionName));
    }

    public static TheoryData<string, Action<ProviderWorkerOptions>> InvalidWorkerSettings() => new()
    {
        { "Concurrency", o => o.Concurrency = 0 },
        { "Concurrency", o => o.Concurrency = 17 },
        { "MaxAttempts", o => o.MaxAttempts = 0 },
        { "MaxAttempts", o => o.MaxAttempts = 21 },
        { "CallTimeout", o => o.CallTimeout = TimeSpan.FromMilliseconds(99) },
        { "CallTimeout", o => o.CallTimeout = TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)) },
        { "CircuitBreaker", o => o.CircuitBreaker = null! },
        { "CircuitBreaker:FailureRatio", o => o.CircuitBreaker.FailureRatio = 0 },
        { "CircuitBreaker:FailureRatio", o => o.CircuitBreaker.FailureRatio = 1.01 },
        { "CircuitBreaker:MinimumThroughput", o => o.CircuitBreaker.MinimumThroughput = 1 },
        { "CircuitBreaker:SamplingDuration", o => o.CircuitBreaker.SamplingDuration = TimeSpan.FromMilliseconds(499) },
        { "CircuitBreaker:BreakDuration", o => o.CircuitBreaker.BreakDuration = TimeSpan.FromHours(2) },
        { "InitialBackoff", o => o.InitialBackoff = TimeSpan.Zero },
        { "InitialBackoff", o => o.MaxBackoff = TimeSpan.FromMilliseconds(1) }
    };

    [Theory]
    [MemberData(nameof(InvalidWorkerSettings))]
    public void A_wrong_worker_setting_is_named_with_its_section(string setting, Action<ProviderWorkerOptions> change)
    {
        var invoice = new InvoiceWorkerOptions();
        var notification = new NotificationWorkerOptions();
        change(invoice);
        change(notification);

        var invoiceResult = new InvoiceWorkerOptionsValidator().Validate(null, invoice);
        var notificationResult = new NotificationWorkerOptionsValidator().Validate(null, notification);

        Assert.StartsWith($"InvoiceWorker:{setting} ", Assert.Single(invoiceResult.Failures!));
        Assert.StartsWith($"NotificationWorker:{setting} ", Assert.Single(notificationResult.Failures!));
    }

    [Fact]
    public void The_limits_of_the_worker_settings_themselves_are_allowed()
    {
        var options = new InvoiceWorkerOptions
        {
            Concurrency = 16,
            MaxAttempts = 20,
            CallTimeout = TimeSpan.FromMilliseconds(100),
            InitialBackoff = TimeSpan.FromSeconds(1),
            MaxBackoff = TimeSpan.FromSeconds(1)
        };
        options.CircuitBreaker.FailureRatio = 1;
        options.CircuitBreaker.MinimumThroughput = 2;

        Assert.True(new InvoiceWorkerOptionsValidator().Validate(null, options).Succeeded);
    }

    // ---- the body of a payment ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(PaymentMethod.Cash, null)]
    [InlineData(PaymentMethod.BankTransfer, "FT26100912345")]
    [InlineData(PaymentMethod.Card, "")]
    [InlineData(PaymentMethod.EWallet, "   ")]
    public void A_payment_needs_a_method_and_takes_an_optional_reference(PaymentMethod method, string? reference)
    {
        Assert.True(new PayOrderRequestValidator().Validate(new PayOrderRequest(method, reference)).IsValid);
    }

    [Fact]
    public void A_payment_without_a_known_method_or_with_a_reference_that_is_too_long_is_refused_on_its_field()
    {
        var validator = new PayOrderRequestValidator();

        var missing = validator.Validate(new PayOrderRequest(null, null));
        var unknown = validator.Validate(new PayOrderRequest((PaymentMethod)42, null));
        var tooLong = validator.Validate(new PayOrderRequest(PaymentMethod.Cash, new string('x', Order.MaxPaymentReferenceLength + 1)));
        // Measured as it is stored: trimmed.
        var padded = validator.Validate(new PayOrderRequest(PaymentMethod.Cash, $"  {new string('x', Order.MaxPaymentReferenceLength)}  "));

        Assert.Equal(nameof(PayOrderRequest.PaymentMethod), Assert.Single(missing.Errors).PropertyName, ignoreCase: true);
        Assert.Equal(nameof(PayOrderRequest.PaymentMethod), Assert.Single(unknown.Errors).PropertyName, ignoreCase: true);
        var failure = Assert.Single(tooLong.Errors);
        Assert.Equal(nameof(PayOrderRequest.PaymentReference), failure.PropertyName, ignoreCase: true);
        Assert.Contains("100", failure.ErrorMessage);
        Assert.True(padded.IsValid);
    }

    [Fact]
    public void The_payment_fields_and_refusals_have_a_name_and_a_text_in_both_languages()
    {
        foreach (var culture in new[] { English, Vietnamese })
        {
            Assert.False(string.IsNullOrWhiteSpace(FieldNames.FindExact("paymentMethod", culture)));
            Assert.False(string.IsNullOrWhiteSpace(FieldNames.FindExact("paymentReference", culture)));
            Assert.False(string.IsNullOrWhiteSpace(ValidationMessages.FindExact(ValidationMessages.Keys.OrderNotPayable, culture)));
            Assert.False(string.IsNullOrWhiteSpace(ValidationMessages.FindExact(ValidationMessages.Keys.OrderAlreadyPaid, culture)));
        }

        Assert.NotEqual(
            ValidationMessages.FindExact(ValidationMessages.Keys.OrderNotPayable, English),
            ValidationMessages.FindExact(ValidationMessages.Keys.OrderAlreadyPaid, English));
    }

    // ---- the messages ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(EventTypes.InvoiceRequested)]
    [InlineData(EventTypes.EmailRequested)]
    public void The_message_of_a_payment_task_names_its_task_in_the_envelope_and_in_the_payload(string eventType)
    {
        var taskId = Guid.CreateVersion7();
        var payload = eventType == EventTypes.InvoiceRequested
            ? MessageJson.Serialize(new InvoiceRequestedPayload(taskId))
            : MessageJson.Serialize(new EmailRequestedPayload(taskId));
        var outbox = new OutboxEvent(eventType, taskId.ToString(), payload, "corr-1", inventoryMovementId: null);

        var envelope = MessageEnvelope.FromOutboxEvent(outbox);
        var body = MessageJson.SerializeToUtf8Bytes(envelope);

        Assert.Equal($$"""{"taskId":"{{taskId}}"}""", payload);
        Assert.Equal((taskId, eventType, outbox.OutboxEventId.ToString()), (envelope.TaskId, envelope.EventType, envelope.MessageId));
        Assert.True(TaskRequestMessageParser.TryParse(body, eventType, out var message, out var rejection));
        Assert.Null(rejection);
        Assert.Equal((taskId, "corr-1"), (message!.TaskId, message.Envelope.CorrelationId));

        // The same bytes on a queue of another event type are poison.
        Assert.False(TaskRequestMessageParser.TryParse(body, EventTypes.OrderCreated, out var other, out var mismatch));
        Assert.Null(other);
        Assert.Equal(MessageRejectionCodes.UnexpectedEventType, mismatch!.Code);
    }

    [Fact]
    public void The_reason_of_a_rejected_message_is_text_for_operators_and_its_code_is_what_the_api_returns()
    {
        Assert.False(TaskRequestMessageParser.TryParse(Encoding.UTF8.GetBytes("{ nope"), EventTypes.InvoiceRequested, out _, out var rejection));

        Assert.Equal(MessageRejectionCodes.InvalidJson, rejection!.Code);
        Assert.StartsWith("Body is not a valid message envelope:", rejection.Reason);
    }
}
