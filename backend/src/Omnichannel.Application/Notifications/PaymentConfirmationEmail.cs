using System.Globalization;

namespace Omnichannel.Application.Notifications;

/// <summary>Bound from the <c>Notifications</c> configuration section of the Workers host.</summary>
public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public const string English = "en";
    public const string Vietnamese = "vi";

    /// <summary>The language of every e-mail to a customer: <c>vi</c> (default) or <c>en</c>.</summary>
    public string Language { get; set; } = Vietnamese;

    /// <summary>IANA id of the zone the times in an e-mail are shown in.</summary>
    public string TimeZone { get; set; } = "Asia/Ho_Chi_Minh";
}

/// <summary>Subject and plain text of an e-mail.</summary>
public sealed record EmailContent(string Subject, string TextBody);

/// <summary>
/// The confirmation a customer gets after paying an order: the order's external id, its lines, the total and when it
/// was paid. Every word comes from <see cref="NotificationTexts"/>; numbers and the time are formatted here, the same
/// way whatever the culture of the thread.
/// </summary>
public static class PaymentConfirmationEmail
{
    public static EmailContent Build(PaidOrderNotice order, DateTimeOffset paidAt, string language, TimeZoneInfo timeZone)
    {
        var culture = CultureInfo.GetCultureInfo(language == NotificationsOptions.English ? NotificationsOptions.English : NotificationsOptions.Vietnamese);

        var lines = order.Lines.Select(line => NotificationTexts.Get(
            NotificationTexts.Keys.PaymentLine,
            culture,
            ("Sku", line.Sku),
            ("ProductName", line.Name),
            ("Quantity", line.Quantity.ToString(CultureInfo.InvariantCulture)),
            ("UnitPrice", Money(line.UnitPrice, culture)),
            ("LineTotal", Money(line.Quantity * line.UnitPrice, culture))));

        var local = TimeZoneInfo.ConvertTime(paidAt, timeZone);
        return new EmailContent(
            NotificationTexts.Get(NotificationTexts.Keys.PaymentSubject, culture, ("ExternalOrderId", order.ExternalOrderId)),
            NotificationTexts.Get(
                NotificationTexts.Keys.PaymentBody,
                culture,
                ("CustomerName", order.CustomerName),
                ("ExternalOrderId", order.ExternalOrderId),
                ("Lines", string.Join("\n", lines)),
                ("Total", Money(order.TotalAmount, culture)),
                // The pattern is the language's; the digits are always the invariant ones.
                ("PaidAt", local.ToString(NotificationTexts.Get(NotificationTexts.Keys.DateTimeFormat, culture), CultureInfo.InvariantCulture)),
                ("TimeZone", timeZone.Id)));
    }

    /// <summary>Whole amounts without decimals, others with up to two; grouped as the language groups numbers.</summary>
    private static string Money(decimal amount, CultureInfo culture) => amount.ToString("#,##0.##", culture);
}
