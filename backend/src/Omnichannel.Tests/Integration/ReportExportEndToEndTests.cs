using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Tests.Integration.Workers;
using Omnichannel.Workers.ReportExportWorker;
using RabbitMQ.Client;
using static Omnichannel.Tests.Integration.ReportExportTesting;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 020: an export through the real path - API → outbox → Outbox Publisher → RabbitMQ → Report Export Worker →
/// object storage - and back to the requester as a download link. The Workers host runs against the API test database
/// and the storage emulator of the API tests; the "browser" is an <see cref="HttpClient"/> without the API's token.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class ReportExportEndToEndTests(ApiFactory factory, RabbitMqBrokerFixture broker) : IClassFixture<RabbitMqBrokerFixture>, IAsyncLifetime
{
    private const string DeadLetterQueue = "report.export.dlq";

    /// <summary>What a browser is for the storage: no <c>Authorization</c> header, nothing of the API.</summary>
    private readonly HttpClient _browser = new();

    private StorageEmulator _storage = null!;

    public async Task InitializeAsync() => _storage = await factory.StorageAsync();

    public Task DisposeAsync()
    {
        _browser.Dispose();
        return Task.CompletedTask;
    }

    // ---- the whole flow ---------------------------------------------------------------------------------------

    public static TheoryData<string, string[], string[], string[], string[]> OrderTexts() => new()
    {
        {
            "en",
            ["Orders", "Items", "Info"],
            ["Created at (Europe/Berlin)", "Channel", "External order ID", "Customer", "Customer phone", "Status", "Lines", "Total amount", "Order ID"],
            ["Order created at (Europe/Berlin)", "External order ID", "Channel", "SKU", "Product name", "Quantity", "Unit price", "Line total", "Order ID"],
            ["Reserved", "Insufficient stock"]
        },
        {
            "vi",
            ["Đơn hàng", "Dòng hàng", "Thông tin"],
            ["Thời điểm tạo (Europe/Berlin)", "Kênh", "Mã đơn hàng bên ngoài", "Khách hàng", "Số điện thoại khách hàng", "Trạng thái", "Số dòng", "Tổng tiền", "Mã đơn hàng"],
            ["Thời điểm tạo đơn (Europe/Berlin)", "Mã đơn hàng bên ngoài", "Kênh", "SKU", "Tên sản phẩm", "Số lượng", "Đơn giá", "Thành tiền", "Mã đơn hàng"],
            ["Đã giữ hàng", "Không đủ hàng"]
        }
    };

    [Theory]
    [MemberData(nameof(OrderTexts))]
    public async Task An_orders_export_is_built_in_the_background_and_downloaded_with_a_link(
        string language, string[] sheets, string[] orderHeaders, string[] itemHeaders, string[] statusLabels)
    {
        var (manager, managerId, managerEmail) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var token = TestData.UniqueSuffix().ToUpperInvariant();
        // A customer whose name looks like a formula, and one that is not exported.
        var customer = await SeedCustomerAsync(factory, "=1+1", "+84 90 000 0001");
        var other = await SeedCustomerAsync(factory, $"Other {token}");
        var products = await SeedProductsAsync(
            factory,
            ($"XL-{token}-A", "@SUM(A1)", 150_000m, 0, 5, null),
            ($"XL-{token}-B", $"Product B {token}", 20_500.5m, 0, 5, null));

        // Europe/Berlin left daylight saving time on 2025-10-26 at 01:00 UTC (03:00 CEST became 02:00 CET).
        var first = await SeedOrderAsync(
            factory, "website", customer.CustomerId, $"XL-{token}-1", OrderStatus.Reserved, new DateTimeOffset(2025, 10, 25, 22, 30, 0, TimeSpan.Zero),
            (products[1].ProductId, 3, 20_500.5m), (products[0].ProductId, 2, 150_000m));
        var second = await SeedOrderAsync(
            factory, "website", customer.CustomerId, $"XL-{token}-2", OrderStatus.InsufficientStock, new DateTimeOffset(2025, 10, 26, 1, 30, 0, TimeSpan.Zero),
            (products[0].ProductId, 1, 150_000m));
        var third = await SeedOrderAsync(
            factory, "website", customer.CustomerId, $"XL-{token}-3", OrderStatus.Reserved, new DateTimeOffset(2025, 10, 26, 12, 0, 0, TimeSpan.Zero),
            (products[0].ProductId, 4, 149_000m));
        // The same search text, but: another channel, another customer, outside the range.
        await SeedOrderAsync(factory, "pos", customer.CustomerId, $"XL-{token}-4", OrderStatus.Reserved, first.CreatedAt, (products[0].ProductId, 1, 1m));
        await SeedOrderAsync(factory, "website", other.CustomerId, $"XL-{token}-5", OrderStatus.Reserved, new DateTimeOffset(2025, 10, 26, 2, 0, 0, TimeSpan.Zero), (products[0].ProductId, 1, 1m));
        await SeedOrderAsync(factory, "website", customer.CustomerId, $"XL-{token}-6", OrderStatus.Reserved, new DateTimeOffset(2025, 10, 27, 0, 0, 1, TimeSpan.Zero), (products[0].ProductId, 1, 1m));

        var before = DateTimeOffset.UtcNow;
        await using var hints = await HintProbe.StartAsync(factory.DatabaseConnectionString);
        var taskId = await RequestAsync(
            manager,
            "/api/reports/orders/export",
            new ExportOrdersRequest(
                " Website ",
                null,
                customer.CustomerId,
                $" xl-{token.ToLowerInvariant()} ",
                new DateTimeOffset(2025, 10, 25, 0, 0, 0, TimeSpan.FromHours(2)),
                new DateTimeOffset(2025, 10, 27, 0, 0, 0, TimeSpan.FromHours(1)),
                "Europe/Berlin"),
            language);

        using var workers = await StartWorkersAsync(factory, broker, "export-e2e-orders");
        try
        {
            await WaitForStatusAsync(factory, taskId, ProcessingTaskStatus.Succeeded);
        }
        finally
        {
            await workers.StopAsync();
        }

        // ---- the task: one attempt by the export worker; every status change was announced ----
        var task = await TaskAsync(factory, taskId);
        var attempt = Assert.Single(task.Attempts);
        Assert.Equal(
            (ProcessingTaskType.ReportExport, 1, ProcessingAttemptStatus.Succeeded, ReportExportMessageHandler.WorkerName, "export-e2e-orders"),
            (task.TaskType, attempt.AttemptNo, attempt.Status, attempt.WorkerName, attempt.WorkerInstanceId));
        foreach (var status in new[] { ProcessingTaskStatus.Pending, ProcessingTaskStatus.InProgress, ProcessingTaskStatus.Succeeded })
        {
            var hint = (await hints.WaitForTaskAsync(taskId, status.ToString())).Events.First(e => e.TaskId == taskId && e.Status == status.ToString());
            Assert.Equal((Application.Realtime.RealtimeEventTypes.ProcessingTaskChanged, nameof(ProcessingTaskType.ReportExport)), (hint.Type, hint.TaskType));
        }

        // ---- the export as its requester sees it ----
        var detail = await DetailAsync(manager, taskId);
        Assert.Equal((ReportType.Orders, ReportExportStatus.Succeeded, (int?)7, (string?)null), (detail.ReportType, detail.Status, detail.RowCount, detail.ErrorCode));
        Assert.Matches(@"^orders_\d{8}-\d{4}\.xlsx$", detail.FileName);
        Assert.InRange(detail.CompletedAt!.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.InRange(detail.ExpiresAt - detail.CreatedAt, TimeSpan.FromDays(7) - TimeSpan.FromSeconds(5), TimeSpan.FromDays(7));
        Assert.Equal(
            ("website", customer.CustomerId.ToString(), $"xl-{token.ToLowerInvariant()}", "Europe/Berlin", language),
            (detail.Parameters!["channel"]!.GetValue<string>(), detail.Parameters["customerId"]!.GetValue<string>(),
                detail.Parameters["search"]!.GetValue<string>(), detail.Parameters["timeZone"]!.GetValue<string>(),
                detail.Parameters["language"]!.GetValue<string>()));
        Assert.Equal(new DateTimeOffset(2025, 10, 24, 22, 0, 0, TimeSpan.Zero), detail.Parameters["from"]!.GetValue<DateTimeOffset>());

        // ---- the download: a link the browser uses without the API's token ----
        var download = await (await manager.GetAsync($"/api/reports/exports/{taskId}/download")).ReadAsync<ReportDownloadResponse>();
        Assert.Equal(detail.FileName, download.FileName);
        Assert.InRange(download.ExpiresAt, before.AddMinutes(4), DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(1));
        Assert.StartsWith(_storage.ServiceUrl, download.Url, StringComparison.Ordinal);

        using var file = await _browser.GetAsync(download.Url);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(ReportFiles.ContentType, file.Content.Headers.ContentType?.MediaType);
        // The stored object carries the header itself: nothing is added to the link.
        Assert.Equal($"attachment; filename=\"{detail.FileName}\"", Assert.Single(file.Content.Headers.GetValues("Content-Disposition")));
        Assert.DoesNotContain("response-content-disposition", download.Url, StringComparison.OrdinalIgnoreCase);
        var bytes = await file.Content.ReadAsByteArrayAsync();
        Assert.Equal(detail.SizeBytes, bytes.LongLength);
        Assert.Equal("PK"u8.ToArray(), bytes[..2]);

        // The same object without a signature is not served.
        using var unsigned = await _browser.GetAsync($"{_storage.ServiceUrl}/{StorageEmulator.Bucket}/{ReportExport.ObjectKeyOf(taskId)}");
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);

        // ---- the workbook ----
        using var workbook = Open(bytes);
        Assert.Equal(sheets, workbook.Worksheets.Select(w => w.Name));

        var orders = workbook.Worksheet(sheets[0]);
        Assert.Equal(orderHeaders, RowTexts(orders, 1, 9));
        Assert.True(orders.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(1, orders.SheetView.SplitRow);
        Assert.True(orders.AutoFilter.IsEnabled);
        Assert.Equal("A1:I4", orders.AutoFilter.Range.RangeAddress.ToString());
        Assert.Equal(4, orders.LastRowUsed()!.RowNumber());

        // Orders by creation time. Times are what a clock in Berlin showed: +02:00 before the change, +01:00 after it.
        Assert.Equal(
            [new DateTime(2025, 10, 26, 0, 30, 0), new DateTime(2025, 10, 26, 2, 30, 0), new DateTime(2025, 10, 26, 13, 0, 0)],
            new[] { 2, 3, 4 }.Select(row => orders.Cell(row, 1).GetDateTime()));
        Assert.All(new[] { 2, 3, 4 }, row => Assert.Equal(XLDataType.DateTime, orders.Cell(row, 1).DataType));
        Assert.Equal("yyyy-mm-dd hh:mm:ss", orders.Cell(2, 1).Style.NumberFormat.Format);
        Assert.Equal(
            ["Website", $"XL-{token}-1", "=1+1", "+84 90 000 0001", statusLabels[0]],
            RowTexts(orders, 2, 6)[1..]);
        Assert.Equal(statusLabels[1], orders.Cell(3, 6).GetString());
        Assert.Equal([first.OrderId.ToString(), second.OrderId.ToString(), third.OrderId.ToString()], new[] { 2, 3, 4 }.Select(row => orders.Cell(row, 9).GetString()));

        // The formula-looking name is text - a value, not a formula.
        var name = orders.Cell(2, 4);
        Assert.Equal((XLDataType.Text, false, "=1+1"), (name.DataType, name.HasFormula, name.GetText()));

        // Numbers are numbers; money has its format.
        Assert.Equal((XLDataType.Number, 2d), (orders.Cell(2, 7).DataType, orders.Cell(2, 7).GetDouble()));
        Assert.Equal((XLDataType.Number, 361_501.5d, "#,##0"), (orders.Cell(2, 8).DataType, orders.Cell(2, 8).GetDouble(), orders.Cell(2, 8).Style.NumberFormat.Format));

        var items = workbook.Worksheet(sheets[1]);
        Assert.Equal(itemHeaders, RowTexts(items, 1, 9));
        Assert.Equal("A1:I5", items.AutoFilter.Range.RangeAddress.ToString());
        // Lines in the order of their orders, then by SKU.
        Assert.Equal(
            [$"XL-{token}-1|XL-{token}-A", $"XL-{token}-1|XL-{token}-B", $"XL-{token}-2|XL-{token}-A", $"XL-{token}-3|XL-{token}-A"],
            new[] { 2, 3, 4, 5 }.Select(row => $"{items.Cell(row, 2).GetString()}|{items.Cell(row, 4).GetString()}"));
        var productName = items.Cell(2, 5);
        Assert.Equal((XLDataType.Text, false, "@SUM(A1)"), (productName.DataType, productName.HasFormula, productName.GetText()));
        Assert.Equal(new DateTime(2025, 10, 26, 0, 30, 0), items.Cell(2, 1).GetDateTime());
        Assert.Equal((3d, 20_500.5d, 61_501.5d), (items.Cell(3, 6).GetDouble(), items.Cell(3, 7).GetDouble(), items.Cell(3, 8).GetDouble()));
        Assert.False(items.Cell(3, 8).HasFormula);
        Assert.Equal("#,##0", items.Cell(3, 8).Style.NumberFormat.Format);

        // Info: the filters as entered, with names instead of ids; the requester by e-mail; the row counts.
        var info = workbook.Worksheet(sheets[2]);
        var values = Enumerable.Range(1, info.LastRowUsed()!.RowNumber()).ToDictionary(row => row, row => info.Cell(row, 2));
        Assert.Equal(
            ["Website", "=1+1", $"xl-{token.ToLowerInvariant()}", "Europe/Berlin", managerEmail],
            new[] { 2, 4, 5, 8, 10 }.Select(row => values[row].GetString()));
        Assert.Equal((XLDataType.Text, false), (values[4].DataType, values[4].HasFormula));
        Assert.Equal(
            (new DateTime(2025, 10, 25, 0, 0, 0), new DateTime(2025, 10, 27, 0, 0, 0)),
            (values[6].GetDateTime(), values[7].GetDateTime()));
        Assert.Equal((3d, 4d), (values[11].GetDouble(), values[12].GetDouble()));

        // Nothing but the name and the phone of a customer leaves the system.
        var everything = string.Join('\n', workbook.Worksheets.SelectMany(w => w.CellsUsed()).Select(c => c.GetString()));
        Assert.DoesNotContain("private@customer.test", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("1 Private Street", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(managerId.ToString(), everything, StringComparison.Ordinal);
    }
}
