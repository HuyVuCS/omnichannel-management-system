using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Realtime;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.Persistence.Alerts;

namespace Omnichannel.Tests.Alerts;

/// <summary>Task 017: what an alert is about (its key) and what an occurrence carries per alert type.</summary>
public sealed class AlertKeyTests
{
    private static readonly Guid Product = Guid.Parse("0199a1b2-0000-7000-8000-00000000AB01");
    private static readonly Guid Channel = Guid.Parse("0199a1b2-0000-7000-8000-00000000AB02");
    private static readonly Guid Task = Guid.Parse("0199a1b2-0000-7000-8000-00000000AB03");
    private static readonly Guid Order = Guid.Parse("0199a1b2-0000-7000-8000-00000000AB04");

    [Fact]
    public void Keys_name_the_type_and_the_ids_without_dashes_in_lower_case()
    {
        Assert.Equal("low_stock:0199a1b200007000800000000000ab01", AlertDedupKey.LowStock(Product));
        Assert.Equal("insufficient_stock:0199a1b200007000800000000000ab01", AlertDedupKey.InsufficientStock(Product));
        Assert.Equal(
            "stock_sync_failed:0199a1b200007000800000000000ab02:0199a1b200007000800000000000ab01",
            AlertDedupKey.StockSyncFailed(Channel, Product));
        Assert.Equal("processing_failed:0199a1b200007000800000000000ab03", AlertDedupKey.ProcessingFailed(Task));
    }

    [Fact]
    public void Keys_of_different_types_and_subjects_never_collide_and_fit_the_column()
    {
        var other = Guid.NewGuid();
        string[] keys =
        [
            AlertDedupKey.LowStock(Product),
            AlertDedupKey.LowStock(other),
            AlertDedupKey.InsufficientStock(Product),
            AlertDedupKey.StockSyncFailed(Channel, Product),
            AlertDedupKey.StockSyncFailed(Product, Channel), // channel and product are not interchangeable
            AlertDedupKey.StockSyncFailed(other, Product),
            AlertDedupKey.ProcessingFailed(Task),
            AlertDedupKey.ProcessingFailed(Product)
        ];

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.True(key.Length <= Alert.MaxDedupKeyLength, key));
    }

    [Fact]
    public void Insufficient_stock_is_about_the_product_and_carries_the_order()
    {
        var occurrence = AlertOccurrence.InsufficientStock(Product, Order);

        Assert.Equal(
            new AlertOccurrence(AlertType.InsufficientStock, AlertDedupKey.InsufficientStock(Product), Product, null, null, Order, null),
            occurrence);
    }

    [Fact]
    public void Stock_sync_failure_is_about_channel_and_product_and_carries_the_task_and_its_error()
    {
        var occurrence = AlertOccurrence.StockSyncFailed(Channel, Product, Task, "channel_rejected");

        Assert.Equal(
            new AlertOccurrence(AlertType.StockSyncFailed, AlertDedupKey.StockSyncFailed(Channel, Product), Product, Channel, Task, null, "channel_rejected"),
            occurrence);
    }

    [Fact]
    public void Processing_failure_is_about_the_task_and_carries_its_order_when_it_has_one()
    {
        Assert.Equal(
            new AlertOccurrence(AlertType.ProcessingFailed, AlertDedupKey.ProcessingFailed(Task), null, null, Task, Order, "check_violation"),
            AlertOccurrence.ProcessingFailed(Task, Order, "check_violation"));
        Assert.Null(AlertOccurrence.ProcessingFailed(Task, null, "timeout").OrderId);
    }

    [Fact]
    public void Error_code_longer_than_the_column_is_cut()
    {
        var occurrence = AlertOccurrence.ProcessingFailed(Task, null, new string('e', Alert.MaxErrorCodeLength + 20));

        Assert.Equal(new string('e', Alert.MaxErrorCodeLength), occurrence.ErrorCode);
    }
}

/// <summary>Task 017: the two alert hints and their place in the notification payload of task 015.</summary>
public sealed class AlertHintTests
{
    private static readonly Guid Alert = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Channel = Guid.NewGuid();

    [Fact]
    public void Event_type_names_are_the_documented_ones()
    {
        Assert.Equal("alert.changed", RealtimeEventTypes.AlertChanged);
        Assert.Equal("low_stock.created", RealtimeEventTypes.LowStockCreated);
    }

    [Fact]
    public void Alert_changed_carries_id_type_status_product_and_channel_and_nothing_else()
    {
        var hint = RealtimeHint.AlertChanged(Alert, AlertType.StockSyncFailed, AlertStatus.Acknowledged, Product, Channel);

        var json = JsonNode.Parse(new RealtimeHintBatch([hint]).ToPayload())!["events"]![0]!.AsObject();

        Assert.Equal(
            ["alertId", "alertType", "channelId", "productId", "status", "type"],
            json.Select(member => member.Key).Order(StringComparer.Ordinal));
        Assert.Equal("alert.changed", json["type"]!.GetValue<string>());
        Assert.Equal(Alert, json["alertId"]!.GetValue<Guid>());
        Assert.Equal("StockSyncFailed", json["alertType"]!.GetValue<string>());
        Assert.Equal("Acknowledged", json["status"]!.GetValue<string>());
        Assert.Equal(Product, json["productId"]!.GetValue<Guid>());
        Assert.Equal(Channel, json["channelId"]!.GetValue<Guid>());
    }

    [Fact]
    public void Alert_changed_of_an_alert_without_product_or_channel_leaves_them_out()
    {
        var hint = RealtimeHint.AlertChanged(Alert, AlertType.ProcessingFailed, AlertStatus.Open, productId: null, channelId: null);

        var json = JsonNode.Parse(new RealtimeHintBatch([hint]).ToPayload())!["events"]![0]!.AsObject();

        Assert.Equal(["alertId", "alertType", "status", "type"], json.Select(member => member.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Low_stock_created_carries_the_alert_and_the_product_only()
    {
        var hint = RealtimeHint.LowStockCreated(Alert, Product);

        var json = JsonNode.Parse(new RealtimeHintBatch([hint]).ToPayload())!["events"]![0]!.AsObject();

        Assert.Equal(["alertId", "productId", "type"], json.Select(member => member.Key).Order(StringComparer.Ordinal));
        Assert.Equal("low_stock.created", json["type"]!.GetValue<string>());
        Assert.Equal((Alert, Product), (json["alertId"]!.GetValue<Guid>(), json["productId"]!.GetValue<Guid>()));
    }

    [Fact]
    public void Alert_hints_survive_the_round_trip_through_the_payload()
    {
        RealtimeHint[] hints =
        [
            RealtimeHint.AlertChanged(Alert, AlertType.LowStock, AlertStatus.Open, Product, null),
            RealtimeHint.LowStockCreated(Alert, Product)
        ];

        var parsed = RealtimeHintBatch.TryParse(new RealtimeHintBatch(hints).ToPayload());

        Assert.NotNull(parsed);
        Assert.Equal(hints, parsed.Events);
    }

    [Fact]
    public void Too_many_alert_hints_for_one_notification_are_reduced_to_one_hint_per_type()
    {
        // A stock import of many products that all become low: two hints per product.
        var hints = Enumerable.Range(0, 150)
            .SelectMany(_ =>
            {
                var (alert, product) = (Guid.NewGuid(), Guid.NewGuid());
                return new[]
                {
                    RealtimeHint.AlertChanged(alert, AlertType.LowStock, AlertStatus.Open, product, null),
                    RealtimeHint.LowStockCreated(alert, product)
                };
            })
            .ToList();

        var payload = new RealtimeHintBatch(hints).ToPayload();

        Assert.True(Encoding.UTF8.GetByteCount(payload) <= RealtimeChannel.MaxPayloadBytes);
        Assert.Equal(
            [RealtimeHint.AllOf(RealtimeEventTypes.AlertChanged), RealtimeHint.AllOf(RealtimeEventTypes.LowStockCreated)],
            RealtimeHintBatch.TryParse(payload)!.Events);
    }
}

/// <summary>Task 017: <c>GET /api/alerts</c> query rules. Error keys are the camelCase JSON paths (the test assembly sets the
/// same naming convention as the API).</summary>
public sealed class AlertListQueryValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly AlertListQueryValidator _validator = new();

    [Fact]
    public void Empty_query_is_valid_and_shows_what_needs_attention()
    {
        var query = new AlertListQuery();

        Assert.True(_validator.Validate(query).IsValid);
        Assert.Equal([AlertStatus.Open, AlertStatus.Acknowledged], query.Statuses);
        Assert.Equal((1, PageQuery.DefaultPageSize, (string?)null, (string?)null), (query.Page, query.PageSize, query.SortBy, query.SortDir));
    }

    [Fact]
    public void Statuses_asked_for_replace_the_default_and_an_empty_list_means_the_default()
    {
        Assert.Equal([AlertStatus.Resolved], new AlertListQuery { Status = [AlertStatus.Resolved] }.Statuses);
        Assert.Equal(
            [AlertStatus.Resolved, AlertStatus.Open],
            new AlertListQuery { Status = [AlertStatus.Resolved, AlertStatus.Open] }.Statuses);
        Assert.Equal(AlertListQuery.DefaultStatuses, new AlertListQuery { Status = [] }.Statuses);
    }

    [Fact]
    public void Every_filter_together_is_valid()
    {
        var query = new AlertListQuery
        {
            Status = [AlertStatus.Open, AlertStatus.Acknowledged, AlertStatus.Resolved],
            Type = [AlertType.LowStock, AlertType.InsufficientStock, AlertType.StockSyncFailed, AlertType.ProcessingFailed],
            ProductId = Guid.NewGuid(),
            ChannelId = Guid.NewGuid(),
            From = Now.AddDays(-AlertListQuery.MaxRangeDays),
            To = Now,
            Page = 3,
            PageSize = 100,
            SortBy = "occurrenceCount",
            SortDir = "asc"
        };

        Assert.True(_validator.Validate(query).IsValid);
    }

    [Fact]
    public void Unknown_status_or_type_is_rejected_on_its_position_in_the_list()
    {
        var status = _validator.Validate(new AlertListQuery { Status = [AlertStatus.Open, (AlertStatus)99] });
        var type = _validator.Validate(new AlertListQuery { Type = [(AlertType)99] });

        Assert.Equal("status[1]", Assert.Single(status.Errors).PropertyName);
        Assert.Equal("type[0]", Assert.Single(type.Errors).PropertyName);
    }

    [Theory]
    [InlineData("lastOccurredAt")]
    [InlineData("firstOccurredAt")]
    [InlineData("OCCURRENCECOUNT")]
    public void Sort_whitelist(string sortBy)
    {
        Assert.True(_validator.Validate(new AlertListQuery { SortBy = sortBy }).IsValid);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("type")]
    [InlineData("createdAt")]
    [InlineData("resolvedAt")]
    public void Unknown_sort_field_is_rejected_on_sortBy(string sortBy)
    {
        var result = _validator.Validate(new AlertListQuery { SortBy = sortBy });

        var error = Assert.Single(result.Errors);
        Assert.Equal("sortBy", error.PropertyName);
        Assert.Contains("lastOccurredAt, firstOccurredAt, occurrenceCount", error.ErrorMessage);
    }

    [Fact]
    public void Paging_and_sort_direction_follow_the_shared_rules()
    {
        Assert.False(_validator.Validate(new AlertListQuery { Page = 0 }).IsValid);
        Assert.False(_validator.Validate(new AlertListQuery { PageSize = PageQuery.MaxPageSize + 1 }).IsValid);
        Assert.False(_validator.Validate(new AlertListQuery { SortDir = "down" }).IsValid);
        Assert.True(_validator.Validate(new AlertListQuery { SortDir = "DESC" }).IsValid);
    }

    [Fact]
    public void Range_is_inclusive_and_one_end_alone_is_valid()
    {
        Assert.True(_validator.Validate(new AlertListQuery { From = Now, To = Now }).IsValid);
        Assert.True(_validator.Validate(new AlertListQuery { From = Now.AddYears(-3) }).IsValid);
        Assert.True(_validator.Validate(new AlertListQuery { To = Now }).IsValid);
    }

    [Fact]
    public void Inverted_range_is_rejected_on_to_with_the_shared_message()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            var result = _validator.Validate(new AlertListQuery { From = Now, To = Now.AddSeconds(-1) });

            var error = Assert.Single(result.Errors);
            Assert.Equal(("to", "'To date' must not be earlier than 'From date'."), (error.PropertyName, error.ErrorMessage));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Range_longer_than_92_days_is_rejected_on_to_in_both_languages()
    {
        var query = new AlertListQuery { From = Now.AddDays(-AlertListQuery.MaxRangeDays).AddSeconds(-1), To = Now };
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            var english = Assert.Single(_validator.Validate(query).Errors);
            CultureInfo.CurrentUICulture = new CultureInfo("vi");
            var vietnamese = Assert.Single(_validator.Validate(query).Errors);

            Assert.Equal("to", english.PropertyName);
            Assert.Equal("The range from 'From date' to 'To date' must not be longer than 92 days.", english.ErrorMessage);
            Assert.NotEqual(english.ErrorMessage, vietnamese.ErrorMessage);
            Assert.Contains("92", vietnamese.ErrorMessage);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}

/// <summary>Task 017: the <c>Alerts</c> table as migrations are generated from it. No database connection is opened.</summary>
public sealed class AlertModelTests : IDisposable
{
    private readonly OmnichannelDbContext _context = new(
        new DbContextOptionsBuilder<OmnichannelDbContext>().UseNpgsql("Host=localhost;Database=model_tests").Options);

    public void Dispose() => _context.Dispose();

    [Fact]
    public void One_unresolved_alert_per_key_is_a_partial_unique_index_that_is_not_an_expected_violation()
    {
        var index = Entity().GetIndexes().Single(i => i.GetDatabaseName() == "UX_Alerts_DedupKey_Unresolved");

        Assert.True(index.IsUnique);
        Assert.Equal([nameof(Alert.DedupKey)], index.Properties.Select(p => p.Name));
        Assert.Equal("\"Status\" <> 'Resolved'", index.GetFilter());
        // Every insert names the index as its ON CONFLICT arbiter: no code path can see a 23505 on it.
        Assert.DoesNotContain("UX_Alerts_DedupKey_Unresolved", ExpectedUniqueViolations.ConstraintNames.AsEnumerable());
    }

    [Fact]
    public void Check_constraints_tie_the_lifecycle_columns_together()
    {
        var constraints = Entity().GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql);

        Assert.Equal("\"OccurrenceCount\" >= 1", constraints["CK_Alerts_OccurrenceCount_Positive"]);
        Assert.Equal("(\"Status\" = 'Resolved') = (\"ResolvedAt\" IS NOT NULL)", constraints["CK_Alerts_Resolved_ResolvedAt"]);
        Assert.Equal("(\"AcknowledgedAt\" IS NULL) = (\"AcknowledgedBy\" IS NULL)", constraints["CK_Alerts_Acknowledged_By"]);
    }

    [Fact]
    public void Type_and_status_are_stored_as_strings_and_the_id_is_generated_by_the_application()
    {
        var entity = Entity();

        Assert.Equal(typeof(string), entity.FindProperty(nameof(Alert.Type))!.GetTypeMapping().Converter?.ProviderClrType);
        Assert.Equal(typeof(string), entity.FindProperty(nameof(Alert.Status))!.GetTypeMapping().Converter?.ProviderClrType);
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, entity.FindPrimaryKey()!.Properties.Single().ValueGenerated);
        Assert.Equal(Alert.MaxDedupKeyLength, entity.FindProperty(nameof(Alert.DedupKey))!.GetMaxLength());
        Assert.False(entity.FindProperty(nameof(Alert.DedupKey))!.IsNullable);
    }

    [Fact]
    public void References_are_optional_foreign_keys_without_cascade()
    {
        var foreignKeys = Entity().GetForeignKeys().ToDictionary(fk => fk.Properties.Single().Name);

        Assert.Equal(
            [nameof(Alert.ChannelId), nameof(Alert.OrderId), nameof(Alert.ProductId), nameof(Alert.TaskId)],
            foreignKeys.Keys.Order(StringComparer.Ordinal));
        Assert.All(foreignKeys.Values, fk =>
        {
            Assert.False(fk.IsRequired);
            Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        });
        Assert.Equal(typeof(Product), foreignKeys[nameof(Alert.ProductId)].PrincipalEntityType.ClrType);
        Assert.Equal(typeof(Channel), foreignKeys[nameof(Alert.ChannelId)].PrincipalEntityType.ClrType);
        Assert.Equal(typeof(ProcessingTask), foreignKeys[nameof(Alert.TaskId)].PrincipalEntityType.ClrType);
        Assert.Equal(typeof(Order), foreignKeys[nameof(Alert.OrderId)].PrincipalEntityType.ClrType);
    }

    [Fact]
    public void The_default_list_order_and_the_product_have_an_index()
    {
        var indexes = Entity().GetIndexes().Select(i => string.Join(",", i.Properties.Select(p => p.Name))).ToList();

        Assert.Contains("Status,LastOccurredAt", indexes);
        Assert.Contains(nameof(Alert.ProductId), indexes);
    }

    private Microsoft.EntityFrameworkCore.Metadata.IEntityType Entity() =>
        _context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.FindEntityType(typeof(Alert))!;
}
