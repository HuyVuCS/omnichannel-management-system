using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence.Reports;

namespace Omnichannel.Tests.Reports;

/// <summary>
/// Task 020: what an export request becomes (file name, normalised parameters), the limits of the <c>Reports</c>
/// options, the texts of the files, and the rules of the export entity.
/// </summary>
public sealed partial class ReportContractTests
{
    private static readonly CultureInfo English = CultureInfo.InvariantCulture; // the default .resx
    private static readonly CultureInfo Vietnamese = new("vi");

    // ---- file name ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ReportType.Orders, "2026-10-05T03:30:59Z", "Asia/Ho_Chi_Minh", "orders_20261005-1030.xlsx")]
    [InlineData(ReportType.Inventory, "2026-10-05T03:30:00Z", "Asia/Ho_Chi_Minh", "inventory_20261005-1030.xlsx")]
    // The local day of the zone, not the day in UTC; daylight saving time included.
    [InlineData(ReportType.Orders, "2026-10-04T18:38:00Z", "Asia/Ho_Chi_Minh", "orders_20261005-0138.xlsx")]
    [InlineData(ReportType.Orders, "2026-07-01T22:05:00Z", "Europe/Berlin", "orders_20260702-0005.xlsx")]
    [InlineData(ReportType.Orders, "2026-01-01T22:05:00Z", "Europe/Berlin", "orders_20260101-2305.xlsx")]
    [InlineData(ReportType.Inventory, "2026-12-31T23:59:59+00:00", "UTC", "inventory_20261231-2359.xlsx")]
    public void The_file_name_is_the_report_and_the_local_time_of_the_request(ReportType reportType, string requestedAt, string zone, string expected)
    {
        var name = ReportFileName.For(reportType, DateTimeOffset.Parse(requestedAt, CultureInfo.InvariantCulture), TimeZoneInfo.FindSystemTimeZoneById(zone));

        Assert.Equal(expected, name);
        Assert.Matches("^[a-z]+_[0-9]{8}-[0-9]{4}[.]xlsx$", name);
        Assert.True(name.Length <= ReportExport.MaxFileNameLength);
    }

    [Fact]
    public void The_file_name_is_ascii_whatever_the_culture_of_the_thread()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // A culture with its own digits and calendar must not leak into the name.
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var name = ReportFileName.For(ReportType.Orders, new DateTimeOffset(2026, 10, 5, 3, 30, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

            Assert.Equal("orders_20261005-0330.xlsx", name);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal("attachment; filename=\"orders_20261005-0330.xlsx\"", ReportFiles.ContentDispositionOf("orders_20261005-0330.xlsx"));
    }

    // ---- parameters --------------------------------------------------------------------------------------------

    [Fact]
    public void Order_parameters_are_normalised_and_give_the_filter_of_the_order_list()
    {
        var customerId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(7));
        var to = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.FromHours(7));

        var parameters = OrdersExportParameters.Normalize(
            new ExportOrdersRequest("  Digital-Platform ", OrderStatus.Reserved, customerId, "  web-  ", from, to, " Asia/Ho_Chi_Minh "), "vi");

        Assert.Equal(
            new OrdersExportParameters("digital-platform", OrderStatus.Reserved, customerId, "web-", from, to, "Asia/Ho_Chi_Minh", "vi"),
            parameters);
        // The same instants, written in UTC.
        Assert.Equal((TimeSpan.Zero, TimeSpan.Zero), (parameters.From.Offset, parameters.To.Offset));
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 17, 0, 0, TimeSpan.Zero), parameters.From);
        Assert.Equal(
            new Application.Orders.OrderFilter("digital-platform", OrderStatus.Reserved, customerId, parameters.From, parameters.To, "web-"),
            parameters.ToFilter());

        // What is stored, and read again by the worker: camelCase, the status by name.
        var json = MessageJson.Serialize(parameters);
        Assert.Contains("\"status\":\"Reserved\"", json, StringComparison.Ordinal);
        Assert.Contains("\"timeZone\":\"Asia/Ho_Chi_Minh\"", json, StringComparison.Ordinal);
        Assert.Equal(parameters, MessageJson.Deserialize<OrdersExportParameters>(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_channel_or_search_is_no_filter(string? blank)
    {
        var orders = OrdersExportParameters.Normalize(
            new ExportOrdersRequest(blank, null, null, blank, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "UTC"), "en");
        var inventory = InventoryExportParameters.Normalize(new ExportInventoryRequest(blank, false, "UTC"), "en");

        Assert.Equal(((string?)null, (string?)null, (OrderStatus?)null, (Guid?)null), (orders.Channel, orders.Search, orders.Status, orders.CustomerId));
        Assert.Null(inventory.Search);
        Assert.Equal(new Application.Inventory.InventoryFilter(null, false), inventory.ToFilter());
    }

    [Fact]
    public void Inventory_parameters_are_normalised_and_give_the_filter_of_the_inventory_list()
    {
        var parameters = InventoryExportParameters.Normalize(new ExportInventoryRequest("  bolt ", true, " Europe/Berlin "), "en");

        Assert.Equal(new InventoryExportParameters("bolt", true, "Europe/Berlin", "en"), parameters);
        Assert.Equal(new Application.Inventory.InventoryFilter("bolt", true), parameters.ToFilter());
        Assert.Equal(parameters, MessageJson.Deserialize<InventoryExportParameters>(MessageJson.Serialize(parameters)));
    }

    [Theory]
    [InlineData("vi", "vi")]
    [InlineData("vi-VN", "vi")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("fr", "en")]
    public void The_language_of_an_export_is_the_ui_language_of_its_request_or_english(string uiCulture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(uiCulture);
            Assert.Equal(expected, ReportLanguages.Current());
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        Assert.Equal(expected, ReportLanguages.CultureOf(expected).Name);
        Assert.Equal("en", ReportLanguages.CultureOf(null).Name);
        Assert.Equal("en", ReportLanguages.CultureOf("de").Name);
    }

    // ---- time zones --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Asia/Ho_Chi_Minh", true)]
    [InlineData("Europe/Berlin", true)]
    [InlineData("America/New_York", true)]
    [InlineData("UTC", true)]
    [InlineData(" Asia/Ho_Chi_Minh ", true)]
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("+07:00", false)]
    [InlineData("GMT+7 (Hanoi)", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void A_time_zone_is_an_iana_id_this_host_knows(string? id, bool known)
    {
        Assert.Equal(known, ReportTimeZones.IsKnown(id));
        Assert.Equal(known, ReportTimeZones.TryFind(id, out var zone));
        if (known)
        {
            // A real zone with its rules, not just a name.
            Assert.NotNull(zone);
        }
    }

    [Fact]
    public void A_time_zone_id_longer_than_any_real_one_is_not_looked_up()
    {
        Assert.False(ReportTimeZones.IsKnown(new string('x', ReportTimeZones.MaxIdLength + 1)));
        Assert.Equal(TimeSpan.FromHours(7), Zone("Asia/Ho_Chi_Minh").GetUtcOffset(DateTimeOffset.UtcNow));
        Assert.Equal(
            (TimeSpan.FromHours(1), TimeSpan.FromHours(2)),
            (Zone("Europe/Berlin").GetUtcOffset(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)),
                Zone("Europe/Berlin").GetUtcOffset(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero))));

        static TimeZoneInfo Zone(string id) => ReportTimeZones.TryFind(id, out var zone) ? zone : throw new InvalidOperationException(id);
    }

    // ---- options -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_defaults_are_the_decided_limits_and_they_are_valid()
    {
        var options = new ReportsOptions();

        Assert.Equal(
            (100_000, 3, TimeSpan.FromDays(7), TimeSpan.FromHours(1), 100, TimeSpan.FromMinutes(5), 366),
            (options.MaxRows, options.MaxUnfinishedPerUser, options.Retention, options.CleanupInterval, options.CleanupBatchSize,
                options.DownloadLinkLifetime, options.MaxRangeDays));
        Assert.Empty(ReportsOptionsRules.Validate(options));
        Assert.True(new ReportsOptionsValidator().Validate(null, options).Succeeded);
    }

    public static TheoryData<string, Action<ReportsOptions>> OutOfRange() => new()
    {
        { "MaxRows", o => o.MaxRows = 0 },
        { "MaxRows", o => o.MaxRows = ReportsOptionsRules.MaxRowsLimit + 1 },
        { "MaxUnfinishedPerUser", o => o.MaxUnfinishedPerUser = 0 },
        { "MaxUnfinishedPerUser", o => o.MaxUnfinishedPerUser = 101 },
        { "Retention", o => o.Retention = TimeSpan.FromSeconds(59) },
        { "Retention", o => o.Retention = TimeSpan.FromDays(367) },
        { "CleanupInterval", o => o.CleanupInterval = TimeSpan.Zero },
        { "CleanupInterval", o => o.CleanupInterval = TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1) },
        { "CleanupBatchSize", o => o.CleanupBatchSize = 0 },
        { "CleanupBatchSize", o => o.CleanupBatchSize = 1001 },
        { "DownloadLinkLifetime", o => o.DownloadLinkLifetime = TimeSpan.Zero },
        { "DownloadLinkLifetime", o => o.DownloadLinkLifetime = TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1) },
        { "MaxRangeDays", o => o.MaxRangeDays = 0 },
        { "MaxRangeDays", o => o.MaxRangeDays = 3661 }
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void A_setting_out_of_range_stops_the_host_with_a_message_that_names_it(string setting, Action<ReportsOptions> change)
    {
        var options = new ReportsOptions();
        change(options);

        var failure = Assert.Single(ReportsOptionsRules.Validate(options));
        Assert.StartsWith($"Reports:{setting} must be between ", failure, StringComparison.Ordinal);
        var result = new ReportsOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal([failure], result.Failures);
    }

    [Fact]
    public void The_limits_themselves_are_valid_and_every_wrong_setting_is_reported_at_once()
    {
        var smallest = new ReportsOptions
        {
            MaxRows = 1, MaxUnfinishedPerUser = 1, Retention = TimeSpan.FromMinutes(1), CleanupInterval = TimeSpan.FromSeconds(1),
            CleanupBatchSize = 1, DownloadLinkLifetime = TimeSpan.FromSeconds(1), MaxRangeDays = 1
        };
        var largest = new ReportsOptions
        {
            MaxRows = 1_000_000, MaxUnfinishedPerUser = 100, Retention = TimeSpan.FromDays(366), CleanupInterval = TimeSpan.FromDays(1),
            CleanupBatchSize = 1000, DownloadLinkLifetime = TimeSpan.FromDays(7), MaxRangeDays = 3660
        };
        var wrong = new ReportsOptions
        {
            MaxRows = -1, MaxUnfinishedPerUser = -1, Retention = TimeSpan.Zero, CleanupInterval = TimeSpan.Zero, CleanupBatchSize = -1,
            DownloadLinkLifetime = TimeSpan.Zero, MaxRangeDays = -1
        };

        Assert.Empty(ReportsOptionsRules.Validate(smallest));
        Assert.Empty(ReportsOptionsRules.Validate(largest));
        Assert.Equal(7, ReportsOptionsRules.Validate(wrong).Count);
    }

    // ---- texts -------------------------------------------------------------------------------------------------

    public static TheoryData<string> TextKeys()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(ReportTexts.Keys).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            data.Add((string)field.GetRawConstantValue()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TextKeys))]
    public void Every_text_of_a_file_exists_in_english_and_in_vietnamese_with_the_same_placeholders(string key)
    {
        var english = ReportTexts.FindExact(key, English);
        var vietnamese = ReportTexts.FindExact(key, Vietnamese);

        Assert.False(string.IsNullOrWhiteSpace(english), $"{key}: no English text");
        Assert.False(string.IsNullOrWhiteSpace(vietnamese), $"{key}: no Vietnamese text");
        Assert.Equal(Placeholders(english!), Placeholders(vietnamese!));
        Assert.All(Placeholders(english!), placeholder => Assert.Equal("{TimeZone}", placeholder));
    }

    [Fact]
    public void The_resource_files_define_exactly_the_keys_and_sheet_names_are_valid_in_excel()
    {
        var keys = typeof(ReportTexts.Keys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(keys.Count >= 50);
        foreach (var culture in new[] { English, Vietnamese })
        {
            var resources = new ResourceManager("Omnichannel.Application.Resources.ReportTexts", typeof(ReportTexts).Assembly)
                .GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
                .Cast<DictionaryEntry>()
                .Select(entry => (string)entry.Key)
                .Order(StringComparer.Ordinal);
            Assert.Equal(keys, resources);

            // At most 31 characters, none of : \ / ? * [ ]
            foreach (var sheet in new[] { ReportTexts.Keys.SheetOrders, ReportTexts.Keys.SheetItems, ReportTexts.Keys.SheetInventory, ReportTexts.Keys.SheetInfo })
            {
                var name = ReportTexts.FindExact(sheet, culture)!;
                Assert.InRange(name.Length, 1, 31);
                Assert.Equal(-1, name.IndexOfAny([':', '\\', '/', '?', '*', '[', ']']));
            }
        }
    }

    [Fact]
    public void A_text_is_read_in_the_language_it_is_asked_for_whatever_the_thread_culture()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            // The worker has no request culture: the language is the one stored with the export.
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            Assert.Equal("Thời điểm tạo (Asia/Ho_Chi_Minh)", ReportTexts.Get(ReportTexts.Keys.ColCreatedAt, ReportLanguages.CultureOf("vi"), ("TimeZone", "Asia/Ho_Chi_Minh")));
            CultureInfo.CurrentUICulture = new CultureInfo("vi");
            Assert.Equal("Created at (UTC)", ReportTexts.Get(ReportTexts.Keys.ColCreatedAt, ReportLanguages.CultureOf("en"), ("TimeZone", "UTC")));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        Assert.Throws<InvalidOperationException>(() => ReportTexts.Get("NoSuchKey", English));
        Assert.Throws<InvalidOperationException>(() => ReportTexts.Get(ReportTexts.Keys.ColChannel, English, ("TimeZone", "UTC")));
    }

    // ---- the entity --------------------------------------------------------------------------------------------

    [Fact]
    public void An_export_has_no_result_until_it_is_completed_and_then_all_of_it()
    {
        var taskId = Guid.CreateVersion7();
        var expiresAt = new DateTimeOffset(2026, 10, 12, 3, 30, 0, TimeSpan.Zero);
        var export = new ReportExport(taskId, ReportType.Orders, "{}", "orders_20261005-1030.xlsx", expiresAt);

        Assert.Equal((taskId, ReportType.Orders, "{}", "orders_20261005-1030.xlsx", expiresAt), (export.TaskId, export.ReportType, export.Parameters, export.FileName, export.ExpiresAt));
        Assert.Equal(((string?)null, (long?)null, (int?)null, (DateTimeOffset?)null, (DateTimeOffset?)null), (export.ObjectKey, export.SizeBytes, export.RowCount, export.CompletedAt, export.DeletedAt));

        var completedAt = expiresAt.AddDays(-7);
        export.Complete(2048, 0, completedAt);

        // The key depends on the task alone.
        Assert.Equal(($"exports/{taskId}.xlsx", (long?)2048, (int?)0, (DateTimeOffset?)completedAt), (export.ObjectKey, export.SizeBytes, export.RowCount, export.CompletedAt));
        Assert.Equal(ReportExport.ObjectKeyOf(taskId), export.ObjectKey);
        Assert.Equal(ReportExport.MaxObjectKeyLength, export.ObjectKey!.Length);
    }

    [Fact]
    public void The_first_deletion_counts_and_a_deleted_export_cannot_be_completed()
    {
        var export = new ReportExport(Guid.CreateVersion7(), ReportType.Inventory, "{}", "inventory.xlsx", DateTimeOffset.UtcNow);
        var first = new DateTimeOffset(2026, 10, 12, 4, 0, 0, TimeSpan.Zero);

        export.MarkDeleted(first);
        export.MarkDeleted(first.AddHours(1));

        Assert.Equal(first, export.DeletedAt);
        Assert.Throws<InvalidOperationException>(() => export.Complete(1, 1, first));
    }

    [Fact]
    public void An_export_needs_a_task_parameters_and_a_file_name_that_fits_its_column()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => new ReportExport(Guid.Empty, ReportType.Orders, "{}", "f.xlsx", now));
        Assert.Throws<ArgumentException>(() => new ReportExport(Guid.NewGuid(), ReportType.Orders, " ", "f.xlsx", now));
        Assert.Throws<ArgumentException>(() => new ReportExport(Guid.NewGuid(), ReportType.Orders, "{}", "", now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportExport(Guid.NewGuid(), ReportType.Orders, "{}", new string('f', 101), now));

        var export = new ReportExport(Guid.NewGuid(), ReportType.Orders, "{}", "f.xlsx", now);
        Assert.Throws<ArgumentOutOfRangeException>(() => export.Complete(0, 1, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => export.Complete(1, -1, now));
    }

    private static string[] Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(match => match.Value).Order().ToArray();

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex PlaceholderPattern();
}
