using System.Globalization;
using ClosedXML.Excel;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Excel;

/// <summary>An order as a row of the <i>Orders</i> sheet. Name and phone of the customer only: no e-mail, no address.</summary>
public sealed record OrderExportRow(
    DateTimeOffset CreatedAt,
    string Channel,
    string ExternalOrderId,
    string CustomerName,
    string? CustomerPhone,
    OrderStatus Status,
    int ItemCount,
    decimal TotalAmount,
    Guid OrderId);

/// <summary>An item line as a row of the <i>Items</i> sheet. <paramref name="LineTotal"/> is a value, not a formula.</summary>
public sealed record OrderItemExportRow(
    DateTimeOffset OrderCreatedAt,
    string ExternalOrderId,
    string Channel,
    string Sku,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    Guid OrderId);

/// <summary>A product with its stock as a row of the <i>Inventory</i> sheet.</summary>
public sealed record InventoryExportRow(
    string Sku,
    string Name,
    decimal Price,
    int TotalStock,
    int ReservedStock,
    int AvailableStock,
    int LowStockThreshold,
    bool IsLowStock,
    DateOnly? ExpiryDate,
    DateTimeOffset StockUpdatedAt);

/// <summary>What the <i>Info</i> sheet of an orders export says: the filters as entered, with names instead of ids.</summary>
/// <param name="Channel">The channel's name (the slug as entered when no such channel exists); null = every channel.</param>
/// <param name="Customer">The customer's name; null = every customer.</param>
/// <param name="RequestedBy">E-mail of the requester; null when the user no longer exists.</param>
public sealed record OrdersExportInfo(
    string? Channel,
    OrderStatus? Status,
    string? Customer,
    string? Search,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset GeneratedAt,
    string? RequestedBy);

/// <summary>What the <i>Info</i> sheet of an inventory export says.</summary>
public sealed record InventoryExportInfo(string? Search, bool LowStockOnly, DateTimeOffset GeneratedAt, string? RequestedBy);

/// <summary>
/// The workbook of an orders export, filled while the rows are read: sheet <i>Orders</i> (one row per order), sheet
/// <i>Items</i> (one row per item line) and sheet <i>Info</i>. Texts are in <c>language</c>, times in <c>timeZone</c>
/// (see <see cref="ReportSheet"/> for the cell rules). The whole workbook is held in memory until it is saved.
/// </summary>
public sealed class OrdersWorkbook : IDisposable
{
    private readonly XLWorkbook _workbook = new();
    private readonly CultureInfo _language;
    private readonly TimeZoneInfo _timeZone;
    private readonly string _timeZoneId;
    private readonly ReportSheet _orders;
    private readonly ReportSheet _items;

    /// <param name="timeZoneId">The id as the request named it; it is written into the headers of the time columns.</param>
    public OrdersWorkbook(CultureInfo language, TimeZoneInfo timeZone, string timeZoneId)
    {
        _language = language;
        _timeZone = timeZone;
        _timeZoneId = timeZoneId;
        var zone = ("TimeZone", timeZoneId);

        _orders = new ReportSheet(
            _workbook,
            Text(ReportTexts.Keys.SheetOrders),
            [
                new(Text(ReportTexts.Keys.ColCreatedAt, zone), 20, ReportSheet.DateTimeFormat),
                new(Text(ReportTexts.Keys.ColChannel), 22),
                new(Text(ReportTexts.Keys.ColExternalOrderId), 26),
                new(Text(ReportTexts.Keys.ColCustomerName), 30),
                new(Text(ReportTexts.Keys.ColCustomerPhone), 18),
                new(Text(ReportTexts.Keys.ColStatus), 18),
                new(Text(ReportTexts.Keys.ColItemCount), 10),
                new(Text(ReportTexts.Keys.ColTotalAmount), 16, ReportSheet.MoneyFormat),
                new(Text(ReportTexts.Keys.ColOrderId), 38)
            ],
            timeZone);

        _items = new ReportSheet(
            _workbook,
            Text(ReportTexts.Keys.SheetItems),
            [
                new(Text(ReportTexts.Keys.ColOrderCreatedAt, zone), 20, ReportSheet.DateTimeFormat),
                new(Text(ReportTexts.Keys.ColExternalOrderId), 26),
                new(Text(ReportTexts.Keys.ColChannel), 22),
                new(Text(ReportTexts.Keys.ColSku), 20),
                new(Text(ReportTexts.Keys.ColProductName), 36),
                new(Text(ReportTexts.Keys.ColQuantity), 10),
                new(Text(ReportTexts.Keys.ColUnitPrice), 14, ReportSheet.MoneyFormat),
                new(Text(ReportTexts.Keys.ColLineTotal), 16, ReportSheet.MoneyFormat),
                new(Text(ReportTexts.Keys.ColOrderId), 38)
            ],
            timeZone);
    }

    public int OrderRows => _orders.DataRows;

    public int ItemRows => _items.DataRows;

    public void AddOrder(OrderExportRow order)
    {
        _orders.BeginRow();
        _orders.Moment(1, order.CreatedAt);
        _orders.Text(2, order.Channel);
        _orders.Text(3, order.ExternalOrderId);
        _orders.Text(4, order.CustomerName);
        _orders.Text(5, order.CustomerPhone);
        _orders.Text(6, ReportTexts.StatusLabel(order.Status, _language));
        _orders.Number(7, order.ItemCount);
        _orders.Number(8, order.TotalAmount);
        _orders.Text(9, order.OrderId.ToString());
    }

    public void AddItem(OrderItemExportRow item)
    {
        _items.BeginRow();
        _items.Moment(1, item.OrderCreatedAt);
        _items.Text(2, item.ExternalOrderId);
        _items.Text(3, item.Channel);
        _items.Text(4, item.Sku);
        _items.Text(5, item.ProductName);
        _items.Number(6, item.Quantity);
        _items.Number(7, item.UnitPrice);
        _items.Number(8, item.LineTotal);
        _items.Text(9, item.OrderId.ToString());
    }

    /// <summary>Adds the <i>Info</i> sheet and writes the workbook to <paramref name="destination"/>. Call once, after the last row.</summary>
    public void Save(OrdersExportInfo info, Stream destination)
    {
        _orders.Finish();
        _items.Finish();

        var zone = ("TimeZone", _timeZoneId);
        var all = Text(ReportTexts.Keys.InfoAll);
        var sheet = new InfoSheet(_workbook, Text(ReportTexts.Keys.SheetInfo), _timeZone);
        sheet.Text(Text(ReportTexts.Keys.InfoReport), Text(ReportTexts.Keys.ReportOrders));
        sheet.Text(Text(ReportTexts.Keys.InfoChannel), info.Channel ?? all);
        sheet.Text(Text(ReportTexts.Keys.InfoStatus), info.Status is { } status ? ReportTexts.StatusLabel(status, _language) : all);
        sheet.Text(Text(ReportTexts.Keys.InfoCustomer), info.Customer ?? all);
        sheet.Text(Text(ReportTexts.Keys.InfoSearch), info.Search);
        sheet.Moment(Text(ReportTexts.Keys.InfoFrom, zone), info.From);
        sheet.Moment(Text(ReportTexts.Keys.InfoTo, zone), info.To);
        sheet.Text(Text(ReportTexts.Keys.InfoTimeZone), _timeZoneId);
        sheet.Moment(Text(ReportTexts.Keys.InfoGeneratedAt, zone), info.GeneratedAt);
        sheet.Text(Text(ReportTexts.Keys.InfoRequestedBy), info.RequestedBy);
        sheet.Number(Text(ReportTexts.Keys.InfoOrderRows), OrderRows);
        sheet.Number(Text(ReportTexts.Keys.InfoItemRows), ItemRows);

        _workbook.SaveAs(destination);
    }

    public void Dispose() => _workbook.Dispose();

    private string Text(string key, params (string Name, string Value)[] arguments) => ReportTexts.Get(key, _language, arguments);
}

/// <summary>
/// The workbook of an inventory export: sheet <i>Inventory</i> (one row per product) and sheet <i>Info</i>. Same rules
/// as <see cref="OrdersWorkbook"/>.
/// </summary>
public sealed class InventoryWorkbook : IDisposable
{
    private readonly XLWorkbook _workbook = new();
    private readonly CultureInfo _language;
    private readonly TimeZoneInfo _timeZone;
    private readonly string _timeZoneId;
    private readonly ReportSheet _products;
    private readonly string _yes;
    private readonly string _no;

    /// <param name="timeZoneId">The id as the request named it; it is written into the header of the time column.</param>
    public InventoryWorkbook(CultureInfo language, TimeZoneInfo timeZone, string timeZoneId)
    {
        _language = language;
        _timeZone = timeZone;
        _timeZoneId = timeZoneId;
        _yes = Text(ReportTexts.Keys.Yes);
        _no = Text(ReportTexts.Keys.No);

        _products = new ReportSheet(
            _workbook,
            Text(ReportTexts.Keys.SheetInventory),
            [
                new(Text(ReportTexts.Keys.ColSku), 20),
                new(Text(ReportTexts.Keys.ColProductName), 36),
                new(Text(ReportTexts.Keys.ColPrice), 14, ReportSheet.MoneyFormat),
                new(Text(ReportTexts.Keys.ColTotalStock), 10),
                new(Text(ReportTexts.Keys.ColReservedStock), 10),
                new(Text(ReportTexts.Keys.ColAvailableStock), 10),
                new(Text(ReportTexts.Keys.ColLowStockThreshold), 12),
                new(Text(ReportTexts.Keys.ColLowStock), 10),
                new(Text(ReportTexts.Keys.ColExpiryDate), 14, ReportSheet.DateFormat),
                new(Text(ReportTexts.Keys.ColStockUpdatedAt, ("TimeZone", timeZoneId)), 20, ReportSheet.DateTimeFormat)
            ],
            timeZone);
    }

    public int ProductRows => _products.DataRows;

    public void AddProduct(InventoryExportRow product)
    {
        _products.BeginRow();
        _products.Text(1, product.Sku);
        _products.Text(2, product.Name);
        _products.Number(3, product.Price);
        _products.Number(4, product.TotalStock);
        _products.Number(5, product.ReservedStock);
        _products.Number(6, product.AvailableStock);
        _products.Number(7, product.LowStockThreshold);
        _products.Text(8, product.IsLowStock ? _yes : _no);
        _products.Date(9, product.ExpiryDate);
        _products.Moment(10, product.StockUpdatedAt);
    }

    /// <summary>Adds the <i>Info</i> sheet and writes the workbook to <paramref name="destination"/>. Call once, after the last row.</summary>
    public void Save(InventoryExportInfo info, Stream destination)
    {
        _products.Finish();

        var sheet = new InfoSheet(_workbook, Text(ReportTexts.Keys.SheetInfo), _timeZone);
        sheet.Text(Text(ReportTexts.Keys.InfoReport), Text(ReportTexts.Keys.ReportInventory));
        sheet.Text(Text(ReportTexts.Keys.InfoSearch), info.Search);
        sheet.Text(Text(ReportTexts.Keys.InfoLowStockOnly), info.LowStockOnly ? _yes : _no);
        sheet.Text(Text(ReportTexts.Keys.InfoTimeZone), _timeZoneId);
        sheet.Moment(Text(ReportTexts.Keys.InfoGeneratedAt, ("TimeZone", _timeZoneId)), info.GeneratedAt);
        sheet.Text(Text(ReportTexts.Keys.InfoRequestedBy), info.RequestedBy);
        sheet.Number(Text(ReportTexts.Keys.InfoProductRows), ProductRows);

        _workbook.SaveAs(destination);
    }

    public void Dispose() => _workbook.Dispose();

    private string Text(string key, params (string Name, string Value)[] arguments) => ReportTexts.Get(key, _language, arguments);
}
