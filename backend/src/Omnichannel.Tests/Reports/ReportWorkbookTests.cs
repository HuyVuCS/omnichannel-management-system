using ClosedXML.Excel;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Excel;

namespace Omnichannel.Tests.Reports;

/// <summary>
/// Task 020 C5: the workbooks of the exports, checked on the file as it is opened again - what a user's Excel reads.
/// </summary>
public sealed class ReportWorkbookTests
{
    private const string Berlin = "Europe/Berlin";

    private static readonly DateTimeOffset Noon = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly OrdersExportInfo NoFilters = new(null, null, null, null, Noon.AddDays(-1), Noon, Noon, "manager@example.com");

    /// <summary>Texts that a spreadsheet would run as a formula if they were not stored as text.</summary>
    public static TheoryData<string> FormulaLookingTexts() => new()
    {
        "=1+1", "@SUM(A1)", "+1-1", "-2+3", "=HYPERLINK(\"http://evil.example\",\"click\")", "=cmd|' /C calc'!A0", "\t=1+1",
        // A leading apostrophe is content too (the library would take it for Excel's text mark and drop it).
        "'=1+1", "''quoted", "'s-Hertogenbosch"
    };

    // ---- headers per language ----------------------------------------------------------------------------------

    public static TheoryData<string, string[], string[], string[]> OrderTexts() => new()
    {
        {
            "en",
            ["Orders", "Items", "Info"],
            ["Created at (Europe/Berlin)", "Channel", "External order ID", "Customer", "Customer phone", "Status", "Lines", "Total amount", "Order ID"],
            ["Order created at (Europe/Berlin)", "External order ID", "Channel", "SKU", "Product name", "Quantity", "Unit price", "Line total", "Order ID"]
        },
        {
            "vi",
            ["Đơn hàng", "Dòng hàng", "Thông tin"],
            ["Thời điểm tạo (Europe/Berlin)", "Kênh", "Mã đơn hàng bên ngoài", "Khách hàng", "Số điện thoại khách hàng", "Trạng thái", "Số dòng", "Tổng tiền", "Mã đơn hàng"],
            ["Thời điểm tạo đơn (Europe/Berlin)", "Mã đơn hàng bên ngoài", "Kênh", "SKU", "Tên sản phẩm", "Số lượng", "Đơn giá", "Thành tiền", "Mã đơn hàng"]
        }
    };

    [Theory]
    [MemberData(nameof(OrderTexts))]
    public void An_orders_workbook_has_its_sheets_and_headers_in_the_language_of_the_export(
        string language, string[] sheets, string[] orderHeaders, string[] itemHeaders)
    {
        using var workbook = Orders(language, _ => { });

        Assert.Equal(sheets, workbook.Worksheets.Select(w => w.Name));
        Assert.Equal(orderHeaders, Row(workbook.Worksheet(1), 1, 9));
        Assert.Equal(itemHeaders, Row(workbook.Worksheet(2), 1, 9));

        // Header: bold, frozen, with a filter - also when there is not a single row below it.
        foreach (var sheet in workbook.Worksheets.Take(2))
        {
            Assert.All(Enumerable.Range(1, 9), column => Assert.True(sheet.Cell(1, column).Style.Font.Bold));
            Assert.Equal(1, sheet.SheetView.SplitRow);
            Assert.Equal("A1:I1", sheet.AutoFilter.Range.RangeAddress.ToString());
            Assert.Equal(1, sheet.LastRowUsed()!.RowNumber());
        }
    }

    [Theory]
    [InlineData("en", "Inventory", "Info", new[]
    {
        "SKU", "Product name", "Price", "Total", "Reserved", "Available", "Low-stock threshold", "Low stock", "Expiry date", "Stock updated at (Asia/Ho_Chi_Minh)"
    })]
    [InlineData("vi", "Tồn kho", "Thông tin", new[]
    {
        "SKU", "Tên sản phẩm", "Giá", "Tổng tồn", "Đang giữ", "Khả dụng", "Ngưỡng cảnh báo tồn kho", "Sắp hết hàng", "Hạn dùng", "Cập nhật tồn kho lúc (Asia/Ho_Chi_Minh)"
    })]
    public void An_inventory_workbook_has_its_sheets_and_headers_in_the_language_of_the_export(string language, string data, string info, string[] headers)
    {
        using var workbook = Inventory(language, _ => { });

        Assert.Equal([data, info], workbook.Worksheets.Select(w => w.Name));
        var sheet = workbook.Worksheet(1);
        Assert.Equal(headers, Row(sheet, 1, 10));
        Assert.All(Enumerable.Range(1, 10), column => Assert.True(sheet.Cell(1, column).Style.Font.Bold));
        Assert.Equal(1, sheet.SheetView.SplitRow);
        Assert.Equal("A1:J1", sheet.AutoFilter.Range.RangeAddress.ToString());
    }

    // ---- cell types and formats --------------------------------------------------------------------------------

    [Fact]
    public void Order_cells_are_typed_text_numbers_with_the_money_format_and_date_times()
    {
        var orderId = Guid.NewGuid();
        using var workbook = Orders("en", w =>
        {
            w.AddOrder(new OrderExportRow(Noon, "Website", "WEB-001", "Nguyễn Văn A", "0901 234 567", OrderStatus.Reserved, 2, 1_234_567.5m, orderId));
            w.AddOrder(new OrderExportRow(Noon.AddHours(1), "POS", "POS-002", "Trần B", null, OrderStatus.InsufficientStock, 1, 0m, Guid.NewGuid()));
            w.AddItem(new OrderItemExportRow(Noon, "WEB-001", "Website", "SKU-1", "Bolt M8", 3, 411_522.5m, 1_234_567.5m, orderId));
        });

        var orders = workbook.Worksheet("Orders");
        Assert.Equal("A1:I3", orders.AutoFilter.Range.RangeAddress.ToString());
        Assert.Equal((XLDataType.DateTime, new DateTime(2026, 9, 15, 12, 0, 0), "yyyy-mm-dd hh:mm:ss"), TypedDate(orders.Cell(2, 1)));
        Assert.Equal(["Website", "WEB-001", "Nguyễn Văn A", "0901 234 567", "Reserved"], Row(orders, 2, 6)[1..]);
        Assert.All(new[] { 2, 3, 4, 5, 6, 9 }, column => Assert.Equal(XLDataType.Text, orders.Cell(2, column).DataType));
        Assert.Equal((XLDataType.Number, 2d), (orders.Cell(2, 7).DataType, orders.Cell(2, 7).GetDouble()));
        Assert.Equal(
            (XLDataType.Number, 1_234_567.5d, "#,##0"),
            (orders.Cell(2, 8).DataType, orders.Cell(2, 8).GetDouble(), orders.Cell(2, 8).Style.NumberFormat.Format));
        Assert.Equal(orderId.ToString(), orders.Cell(2, 9).GetText());
        // A customer without a phone has an empty cell; an amount of zero is the number 0. A data row is not bold.
        Assert.True(orders.Cell(3, 5).IsEmpty());
        Assert.Equal((XLDataType.Number, 0d, "Insufficient stock"), (orders.Cell(3, 8).DataType, orders.Cell(3, 8).GetDouble(), orders.Cell(3, 6).GetText()));
        Assert.False(orders.Cell(2, 2).Style.Font.Bold);

        var items = workbook.Worksheet("Items");
        Assert.Equal("A1:I2", items.AutoFilter.Range.RangeAddress.ToString());
        Assert.Equal((XLDataType.DateTime, new DateTime(2026, 9, 15, 12, 0, 0), "yyyy-mm-dd hh:mm:ss"), TypedDate(items.Cell(2, 1)));
        Assert.Equal(["WEB-001", "Website", "SKU-1", "Bolt M8"], Row(items, 2, 5)[1..]);
        Assert.Equal((3d, 411_522.5d, 1_234_567.5d), (items.Cell(2, 6).GetDouble(), items.Cell(2, 7).GetDouble(), items.Cell(2, 8).GetDouble()));
        // The line total is the value it was given, never a formula over the two cells before it.
        Assert.All(new[] { 6, 7, 8 }, column => Assert.Equal((XLDataType.Number, false), (items.Cell(2, column).DataType, items.Cell(2, column).HasFormula)));
        Assert.Equal(("#,##0", "#,##0"), (items.Cell(2, 7).Style.NumberFormat.Format, items.Cell(2, 8).Style.NumberFormat.Format));
    }

    [Fact]
    public void Inventory_cells_are_typed_and_low_stock_is_a_label_in_the_language_of_the_export()
    {
        using var english = Inventory("en", w =>
        {
            w.AddProduct(new InventoryExportRow("SKU-1", "Bolt M8", 12_500m, 10, 4, 6, 2, false, new DateOnly(2027, 1, 31), Noon));
            w.AddProduct(new InventoryExportRow("SKU-2", "Nut M8", 0m, 0, 0, 0, 0, true, null, Noon));
        });
        using var vietnamese = Inventory("vi", w => w.AddProduct(new InventoryExportRow("SKU-2", "Nut M8", 0m, 0, 0, 0, 0, true, null, Noon)));

        var sheet = english.Worksheet("Inventory");
        Assert.Equal("A1:J3", sheet.AutoFilter.Range.RangeAddress.ToString());
        Assert.Equal((XLDataType.Text, "SKU-1", XLDataType.Text, "Bolt M8"), (sheet.Cell(2, 1).DataType, sheet.Cell(2, 1).GetText(), sheet.Cell(2, 2).DataType, sheet.Cell(2, 2).GetText()));
        Assert.Equal((XLDataType.Number, 12_500d, "#,##0"), (sheet.Cell(2, 3).DataType, sheet.Cell(2, 3).GetDouble(), sheet.Cell(2, 3).Style.NumberFormat.Format));
        Assert.Equal([10d, 4d, 6d, 2d], new[] { 4, 5, 6, 7 }.Select(column => sheet.Cell(2, column).GetDouble()));
        Assert.All(new[] { 4, 5, 6, 7 }, column => Assert.Equal(XLDataType.Number, sheet.Cell(2, column).DataType));
        Assert.Equal(("No", "Yes"), (sheet.Cell(2, 8).GetText(), sheet.Cell(3, 8).GetText()));
        // A date is a date (no time of day, no zone); a product without one has an empty cell.
        Assert.Equal((XLDataType.DateTime, new DateTime(2027, 1, 31), "yyyy-mm-dd"), TypedDate(sheet.Cell(2, 9)));
        Assert.True(sheet.Cell(3, 9).IsEmpty());
        Assert.Equal((XLDataType.DateTime, new DateTime(2026, 9, 15, 17, 0, 0), "yyyy-mm-dd hh:mm:ss"), TypedDate(sheet.Cell(2, 10)));

        Assert.Equal("Có", vietnamese.Worksheet("Tồn kho").Cell(2, 8).GetText());
    }

    // ---- time zones --------------------------------------------------------------------------------------------

    [Theory]
    // Europe/Berlin: +01:00 in winter, +02:00 in summer; the change is at 01:00 UTC.
    [InlineData(Berlin, "2026-01-15T10:00:00Z", "2026-01-15T11:00:00")]
    [InlineData(Berlin, "2026-07-15T10:00:00Z", "2026-07-15T12:00:00")]
    [InlineData(Berlin, "2026-03-29T00:59:59Z", "2026-03-29T01:59:59")]
    [InlineData(Berlin, "2026-03-29T01:00:00Z", "2026-03-29T03:00:00")]
    [InlineData(Berlin, "2026-10-25T00:30:00Z", "2026-10-25T02:30:00")]
    [InlineData(Berlin, "2026-10-25T01:30:00Z", "2026-10-25T02:30:00")]
    // The instant, whatever offset it was given with.
    [InlineData(Berlin, "2026-07-15T17:00:00+07:00", "2026-07-15T12:00:00")]
    [InlineData("Asia/Ho_Chi_Minh", "2026-12-31T18:30:00Z", "2027-01-01T01:30:00")]
    [InlineData("UTC", "2026-07-15T10:00:00+02:00", "2026-07-15T08:00:00")]
    public void A_point_in_time_is_written_as_the_clock_of_the_requested_zone_showed_it(string zone, string instant, string expected)
    {
        var moment = DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);
        var local = DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);

        using var orders = Orders(
            "en",
            w =>
            {
                w.AddOrder(new OrderExportRow(moment, "c", "e", "n", null, OrderStatus.Reserved, 1, 1m, Guid.NewGuid()));
                w.AddItem(new OrderItemExportRow(moment, "e", "c", "s", "p", 1, 1m, 1m, Guid.NewGuid()));
            },
            NoFilters with { From = moment, To = moment, GeneratedAt = moment },
            zone);
        using var inventory = Inventory(
            "en", w => w.AddProduct(new InventoryExportRow("s", "n", 1m, 1, 0, 1, 0, false, null, moment)), new InventoryExportInfo(null, false, moment, null), zone);

        Assert.Equal(local, orders.Worksheet("Orders").Cell(2, 1).GetDateTime());
        Assert.Equal(local, orders.Worksheet("Items").Cell(2, 1).GetDateTime());
        Assert.Equal(local, inventory.Worksheet("Inventory").Cell(2, 10).GetDateTime());
        // The zone is named where the time is: Excel has no time zone of its own.
        Assert.Equal($"Created at ({zone})", orders.Worksheet("Orders").Cell(1, 1).GetText());
        Assert.Equal($"Stock updated at ({zone})", inventory.Worksheet("Inventory").Cell(1, 10).GetText());

        // The Info sheet: range and generation time in the same zone.
        var info = orders.Worksheet("Info");
        Assert.Equal(($"From ({zone})", $"To ({zone})", $"Generated at ({zone})"), (info.Cell(6, 1).GetText(), info.Cell(7, 1).GetText(), info.Cell(9, 1).GetText()));
        Assert.All(new[] { 6, 7, 9 }, row => Assert.Equal((XLDataType.DateTime, local, "yyyy-mm-dd hh:mm:ss"), TypedDate(info.Cell(row, 2))));
        Assert.Equal(zone, info.Cell(8, 2).GetText());
        Assert.Equal(local, inventory.Worksheet("Info").Cell(5, 2).GetDateTime());
    }

    // ---- text is never a formula -------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(FormulaLookingTexts))]
    public void A_text_that_looks_like_a_formula_is_stored_as_text_in_every_text_cell(string text)
    {
        using var orders = Orders(
            "en",
            w =>
            {
                w.AddOrder(new OrderExportRow(Noon, text, text, text, text, OrderStatus.Reserved, 1, 1m, Guid.NewGuid()));
                w.AddItem(new OrderItemExportRow(Noon, text, text, text, text, 1, 1m, 1m, Guid.NewGuid()));
            },
            NoFilters with { Channel = text, Customer = text, Search = text, RequestedBy = text });
        using var inventory = Inventory(
            "en", w => w.AddProduct(new InventoryExportRow(text, text, 1m, 1, 0, 1, 0, false, null, Noon)), new InventoryExportInfo(text, false, Noon, text));

        var cells = new[] { 2, 3, 4, 5 }.Select(column => orders.Worksheet("Orders").Cell(2, column))
            .Concat(new[] { 2, 3, 4, 5 }.Select(column => orders.Worksheet("Items").Cell(2, column)))
            .Concat(new[] { 2, 4, 5, 10 }.Select(row => orders.Worksheet("Info").Cell(row, 2)))
            .Concat(new[] { 1, 2 }.Select(column => inventory.Worksheet("Inventory").Cell(2, column)))
            .Concat(new[] { 2, 6 }.Select(row => inventory.Worksheet("Info").Cell(row, 2)))
            .ToList();

        Assert.Equal(16, cells.Count);
        Assert.All(cells, cell =>
        {
            Assert.Equal((XLDataType.Text, false, string.Empty), (cell.DataType, cell.HasFormula, cell.FormulaA1));
            Assert.Equal(text, cell.GetText());
        });
        // No cell of either file is a formula.
        Assert.DoesNotContain(orders.Worksheets.Concat(inventory.Worksheets).SelectMany(w => w.CellsUsed()), cell => cell.HasFormula);
    }

    // ---- layout ------------------------------------------------------------------------------------------------

    [Fact]
    public void Column_widths_come_from_the_headers_and_never_from_the_data()
    {
        var longName = new string('n', 300);
        using var empty = Orders("vi", _ => { });
        using var full = Orders("vi", w =>
        {
            w.AddOrder(new OrderExportRow(Noon, longName, longName, longName, longName, OrderStatus.Reserved, 1, 1m, Guid.NewGuid()));
            w.AddItem(new OrderItemExportRow(Noon, longName, longName, longName, longName, 1, 1m, 1m, Guid.NewGuid()));
        });

        foreach (var name in new[] { "Đơn hàng", "Dòng hàng" })
        {
            var widths = Enumerable.Range(1, 9).Select(column => empty.Worksheet(name).Column(column).Width).ToList();
            Assert.Equal(widths, Enumerable.Range(1, 9).Select(column => full.Worksheet(name).Column(column).Width));
            // Wide enough for its header and the filter button, in the longer language too.
            Assert.All(Enumerable.Range(1, 9), column =>
                Assert.True(widths[column - 1] >= empty.Worksheet(name).Cell(1, column).GetText().Length + 4, $"{name} column {column}"));
        }
    }

    [Fact]
    public void The_workbook_counts_its_rows_and_the_info_sheet_says_what_was_asked_for()
    {
        using var builder = new OrdersWorkbook(ReportLanguages.CultureOf("vi"), TimeZoneInfo.FindSystemTimeZoneById(Berlin), Berlin);
        Assert.Equal((0, 0), (builder.OrderRows, builder.ItemRows));
        builder.AddOrder(new OrderExportRow(Noon, "c", "e", "n", null, OrderStatus.Reserved, 2, 1m, Guid.NewGuid()));
        builder.AddItem(new OrderItemExportRow(Noon, "e", "c", "s1", "p", 1, 1m, 1m, Guid.NewGuid()));
        builder.AddItem(new OrderItemExportRow(Noon, "e", "c", "s2", "p", 1, 1m, 1m, Guid.NewGuid()));
        Assert.Equal((1, 2), (builder.OrderRows, builder.ItemRows));

        using var filtered = Reopen(stream => builder.Save(
            new OrdersExportInfo("Website", OrderStatus.InsufficientStock, "Nguyễn Văn A", "web-", Noon.AddDays(-1), Noon, Noon, "manager@example.com"), stream));
        using var unfiltered = Orders("vi", _ => { }, NoFilters with { RequestedBy = null });

        var info = filtered.Worksheet("Thông tin");
        Assert.Equal(
            ["Báo cáo", "Kênh", "Trạng thái", "Khách hàng", "Từ khoá tìm kiếm", "Từ (Europe/Berlin)", "Đến (Europe/Berlin)", "Múi giờ",
                "Thời điểm tạo tệp (Europe/Berlin)", "Người yêu cầu", "Số đơn hàng", "Số dòng hàng"],
            Enumerable.Range(1, 12).Select(row => info.Cell(row, 1).GetText()));
        Assert.All(Enumerable.Range(1, 12), row => Assert.True(info.Cell(row, 1).Style.Font.Bold));
        Assert.Equal(
            ["Đơn hàng", "Website", "Không đủ hàng", "Nguyễn Văn A", "web-"],
            Enumerable.Range(1, 5).Select(row => info.Cell(row, 2).GetText()));
        Assert.Equal(("manager@example.com", 1d, 2d), (info.Cell(10, 2).GetText(), info.Cell(11, 2).GetDouble(), info.Cell(12, 2).GetDouble()));
        Assert.Equal((XLDataType.Number, XLDataType.Number), (info.Cell(11, 2).DataType, info.Cell(12, 2).DataType));

        // A filter that was not set says so; no search text and an unknown requester leave their cells empty.
        var none = unfiltered.Worksheet("Thông tin");
        Assert.Equal(["(tất cả)", "(tất cả)", "(tất cả)"], new[] { 2, 3, 4 }.Select(row => none.Cell(row, 2).GetText()));
        Assert.True(none.Cell(5, 2).IsEmpty());
        Assert.True(none.Cell(10, 2).IsEmpty());
        Assert.Equal((0d, 0d), (none.Cell(11, 2).GetDouble(), none.Cell(12, 2).GetDouble()));
    }

    [Fact]
    public void Every_order_status_has_a_label_in_both_languages()
    {
        Assert.All(Enum.GetValues<OrderStatus>(), status =>
        {
            var english = ReportTexts.StatusLabel(status, ReportLanguages.CultureOf("en"));
            var vietnamese = ReportTexts.StatusLabel(status, ReportLanguages.CultureOf("vi"));
            Assert.False(string.IsNullOrWhiteSpace(english));
            Assert.False(string.IsNullOrWhiteSpace(vietnamese));
            Assert.NotEqual(english, vietnamese);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportTexts.StatusLabel((OrderStatus)99, ReportLanguages.CultureOf("en")));
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static XLWorkbook Orders(string language, Action<OrdersWorkbook> fill, OrdersExportInfo? info = null, string zone = Berlin)
    {
        using var builder = new OrdersWorkbook(ReportLanguages.CultureOf(language), TimeZoneInfo.FindSystemTimeZoneById(zone), zone);
        fill(builder);
        return Reopen(stream => builder.Save(info ?? NoFilters, stream));
    }

    private static XLWorkbook Inventory(
        string language, Action<InventoryWorkbook> fill, InventoryExportInfo? info = null, string zone = "Asia/Ho_Chi_Minh")
    {
        using var builder = new InventoryWorkbook(ReportLanguages.CultureOf(language), TimeZoneInfo.FindSystemTimeZoneById(zone), zone);
        fill(builder);
        return Reopen(stream => builder.Save(info ?? new InventoryExportInfo(null, false, Noon, "manager@example.com"), stream));
    }

    /// <summary>Saves and opens the file again: every assertion is on what was written, not on the builder's memory.</summary>
    private static XLWorkbook Reopen(Action<Stream> save)
    {
        var stream = new MemoryStream();
        save(stream);
        stream.Position = 0;
        return new XLWorkbook(stream);
    }

    private static string[] Row(IXLWorksheet sheet, int row, int columns) =>
        Enumerable.Range(1, columns).Select(column => sheet.Cell(row, column).GetString()).ToArray();

    private static (XLDataType Type, DateTime Value, string Format) TypedDate(IXLCell cell) =>
        (cell.DataType, cell.GetDateTime(), cell.Style.NumberFormat.Format);
}
