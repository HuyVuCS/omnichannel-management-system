using System.Globalization;
using System.Resources;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Reports;

/// <summary>
/// Every text an export file carries: sheet names, column headers, labels. Texts live in
/// <c>Resources/ReportTexts.resx</c> (English, the default) and <c>ReportTexts.vi.resx</c>. A file is written in the
/// language stored with its export (the language of the request), not in the culture of the thread that builds it -
/// the worker has none - so the culture is always passed in.
/// </summary>
public static class ReportTexts
{
    private static readonly ResourceManager Resources = new(
        "Omnichannel.Application.Resources.ReportTexts",
        typeof(ReportTexts).Assembly);

    /// <summary>Resource names. A unit test checks that each one has an English and a Vietnamese text.</summary>
    public static class Keys
    {
        public const string SheetOrders = nameof(SheetOrders);
        public const string SheetItems = nameof(SheetItems);
        public const string SheetInventory = nameof(SheetInventory);
        public const string SheetInfo = nameof(SheetInfo);
        public const string ColCreatedAt = nameof(ColCreatedAt);
        public const string ColChannel = nameof(ColChannel);
        public const string ColExternalOrderId = nameof(ColExternalOrderId);
        public const string ColCustomerName = nameof(ColCustomerName);
        public const string ColCustomerPhone = nameof(ColCustomerPhone);
        public const string ColStatus = nameof(ColStatus);
        public const string ColItemCount = nameof(ColItemCount);
        public const string ColTotalAmount = nameof(ColTotalAmount);
        public const string ColOrderId = nameof(ColOrderId);
        public const string ColOrderCreatedAt = nameof(ColOrderCreatedAt);
        public const string ColSku = nameof(ColSku);
        public const string ColProductName = nameof(ColProductName);
        public const string ColQuantity = nameof(ColQuantity);
        public const string ColUnitPrice = nameof(ColUnitPrice);
        public const string ColLineTotal = nameof(ColLineTotal);
        public const string ColPrice = nameof(ColPrice);
        public const string ColTotalStock = nameof(ColTotalStock);
        public const string ColReservedStock = nameof(ColReservedStock);
        public const string ColAvailableStock = nameof(ColAvailableStock);
        public const string ColLowStockThreshold = nameof(ColLowStockThreshold);
        public const string ColLowStock = nameof(ColLowStock);
        public const string ColExpiryDate = nameof(ColExpiryDate);
        public const string ColStockUpdatedAt = nameof(ColStockUpdatedAt);
        public const string Yes = nameof(Yes);
        public const string No = nameof(No);
        public const string StatusPending = nameof(StatusPending);
        public const string StatusReserved = nameof(StatusReserved);
        public const string StatusInsufficientStock = nameof(StatusInsufficientStock);
        public const string StatusCancelled = nameof(StatusCancelled);
        public const string StatusCompleted = nameof(StatusCompleted);
        public const string InfoReport = nameof(InfoReport);
        public const string ReportOrders = nameof(ReportOrders);
        public const string ReportInventory = nameof(ReportInventory);
        public const string InfoChannel = nameof(InfoChannel);
        public const string InfoStatus = nameof(InfoStatus);
        public const string InfoCustomer = nameof(InfoCustomer);
        public const string InfoSearch = nameof(InfoSearch);
        public const string InfoFrom = nameof(InfoFrom);
        public const string InfoTo = nameof(InfoTo);
        public const string InfoLowStockOnly = nameof(InfoLowStockOnly);
        public const string InfoAll = nameof(InfoAll);
        public const string InfoTimeZone = nameof(InfoTimeZone);
        public const string InfoGeneratedAt = nameof(InfoGeneratedAt);
        public const string InfoRequestedBy = nameof(InfoRequestedBy);
        public const string InfoOrderRows = nameof(InfoOrderRows);
        public const string InfoItemRows = nameof(InfoItemRows);
        public const string InfoProductRows = nameof(InfoProductRows);
    }

    /// <summary>The text of <paramref name="key"/> in <paramref name="culture"/>, with <c>{Name}</c> placeholders replaced.</summary>
    public static string Get(string key, CultureInfo culture, params (string Name, string Value)[] arguments)
    {
        var text = Resources.GetString(key, culture)
            ?? throw new InvalidOperationException($"Report text '{key}' is not defined in ReportTexts.resx.");

        foreach (var (name, value) in arguments)
        {
            var placeholder = $"{{{name}}}";
            if (!text.Contains(placeholder, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Report text '{key}' has no placeholder {placeholder}.");
            }

            text = text.Replace(placeholder, value, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>The text stored for exactly <paramref name="culture"/>, without fallback; null when that file lacks the key.</summary>
    public static string? FindExact(string key, CultureInfo culture) =>
        Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(key);

    /// <summary>The label of an order status, as the UI shows it.</summary>
    public static string StatusLabel(OrderStatus status, CultureInfo culture) => Get(
        status switch
        {
            OrderStatus.Pending => Keys.StatusPending,
            OrderStatus.Reserved => Keys.StatusReserved,
            OrderStatus.InsufficientStock => Keys.StatusInsufficientStock,
            OrderStatus.Cancelled => Keys.StatusCancelled,
            OrderStatus.Completed => Keys.StatusCompleted,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No label for this order status.")
        },
        culture);
}
