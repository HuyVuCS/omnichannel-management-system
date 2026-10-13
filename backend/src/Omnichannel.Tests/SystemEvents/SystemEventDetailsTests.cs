using System.Reflection;
using System.Text.Json.Nodes;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Invoices;
using Omnichannel.Application.Notifications;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Products;
using Omnichannel.Application.Reports;
using Omnichannel.Application.SystemEvents;
using Omnichannel.Application.Users;
using Omnichannel.Workers.OutboxPublisher;

namespace Omnichannel.Tests.SystemEvents;

/// <summary>
/// Task 018b Part B: the allowlist of payload fields the Logs/Audit endpoint returns (<see cref="SystemEventDetails"/>).
/// </summary>
public sealed class SystemEventDetailsTests
{
    /// <summary>
    /// Every class that declares SystemEventLog event types: the public static <c>…Events</c> classes of the Application
    /// assembly. A new class must be named that way (and is then found here); a new constant in any of them needs an entry
    /// in the allowlist.
    /// </summary>
    private static readonly Type[] EventClasses = typeof(SystemEventDetails).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: true, IsSealed: true, IsPublic: true } && t.Name.EndsWith("Events", StringComparison.Ordinal))
        .ToArray();

    private static IEnumerable<string> EventTypeConstants() =>
        EventClasses
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void The_event_classes_found_by_reflection_are_the_ones_that_write_events_today()
    {
        // Guards the completeness test below against silently checking nothing (or the wrong classes).
        Assert.Equal(
            new[]
            {
                typeof(AlertEvents), typeof(InventoryEvents), typeof(InvoiceEvents), typeof(NotificationEvents),
                typeof(OrderPaymentEvents), typeof(OrderProcessingEvents), typeof(OutboxEvents), typeof(ProcessingEvents),
                typeof(ProductImageEvents), typeof(ReconciliationEvents), typeof(ReportEvents), typeof(StockSyncEvents), typeof(UserEvents)
            }.Select(t => t.Name),
            EventClasses.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(22, EventTypeConstants().Count());
    }

    [Fact]
    public void Every_event_type_constant_has_an_entry_and_every_entry_is_a_constant()
    {
        Assert.Equal(
            EventTypeConstants().Order(StringComparer.Ordinal),
            SystemEventDetails.AllowedFields.Keys.Order(StringComparer.Ordinal));
        Assert.All(SystemEventDetails.AllowedFields, entry => Assert.NotEmpty(entry.Value));
    }

    [Fact]
    public void The_outbox_publisher_writes_the_constant_of_the_map()
    {
        Assert.Equal(OutboxEvents.PublishFailed, OutboxPublisherService.PublishFailedEventType);
    }

    [Fact]
    public void No_entry_allows_the_outbox_error_or_the_reason_of_a_rejected_message()
    {
        Assert.All(SystemEventDetails.AllowedFields.Values, fields =>
        {
            Assert.DoesNotContain("lastError", fields);
            Assert.DoesNotContain("reason", fields);
        });
        Assert.Equal(["outboxEventId", "eventType", "publishAttempts"], SystemEventDetails.AllowedFields[OutboxEvents.PublishFailed]);
    }

    [Fact]
    public void An_export_request_shows_who_asked_for_which_report_and_never_the_filters()
    {
        // The filters hold whatever the user typed (a search text); the task is in the event's own column.
        Assert.Equal(["reportType", "requestedBy"], SystemEventDetails.AllowedFields[ReportEvents.ExportRequested]);
        Assert.Equal(["messageId", "reasonCode"], SystemEventDetails.AllowedFields[ReportEvents.MessageRejected]);

        var requester = Guid.NewGuid();
        var details = SystemEventDetails.Filter(
            ReportEvents.ExportRequested, $$"""{"reportType":"Orders","requestedBy":"{{requester}}","search":"secret","parameters":[1]}""");

        Assert.Equal(["reportType", "requestedBy"], details!.Select(p => p.Key));
        Assert.Equal(requester, SystemEventDetails.ActorIdOf(details));
    }

    [Fact]
    public void A_reconciliation_run_shows_its_counts_and_the_channels_it_could_not_read()
    {
        Assert.Equal(
            [
                "runId", "checked", "inSync", "drifted", "tasksCreated", "deferred", "failedRetried", "skippedUnfinished",
                "skippedRejected", "unreadable"
            ],
            SystemEventDetails.AllowedFields[ReconciliationEvents.Completed]);

        var details = SystemEventDetails.Filter(
            ReconciliationEvents.Completed,
            """{"runId":"r-1","checked":340,"drifted":12,"unreadable":[{"channel":"POS","errorCode":"channel_unavailable","pairs":7}],"secret":"x"}""");

        Assert.NotNull(details);
        Assert.Equal(["runId", "checked", "drifted", "unreadable"], details.Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""[{"channel":"POS","errorCode":"channel_unavailable","pairs":7}]"""), details["unreadable"]));
        // A run is the system's: it names no user.
        Assert.Null(SystemEventDetails.ActorIdOf(details));
    }

    [Fact]
    public void Every_user_management_event_is_admin_only_and_no_other_event_is()
    {
        var userEvents = typeof(UserEvents).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(5, userEvents.Count);
        Assert.All(userEvents, e => Assert.StartsWith(SystemEventVisibility.AdminOnlyPrefix, e, StringComparison.Ordinal));
        Assert.All(
            EventTypeConstants().Except(userEvents),
            e => Assert.False(e.StartsWith(SystemEventVisibility.AdminOnlyPrefix, StringComparison.Ordinal), e));
    }

    [Fact]
    public void Filter_keeps_the_allowed_fields_with_their_values_and_drops_the_rest()
    {
        var details = SystemEventDetails.Filter(
            OrderProcessingEvents.InsufficientStock,
            """
            {"secret":"x","items":[{"productId":"p-1","requested":5,"available":2,"note":{"deep":true}}],
             "taskId":"t-1","orderId":null,"lastError":"boom"}
            """);

        Assert.NotNull(details);
        // The order of the allowlist, values as stored: nested arrays and objects whole, null kept as null.
        Assert.Equal(["orderId", "taskId", "items"], details.Select(p => p.Key));
        Assert.Null(details["orderId"]);
        Assert.Equal("t-1", details["taskId"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""[{"productId":"p-1","requested":5,"available":2,"note":{"deep":true}}]"""),
            details["items"]));
    }

    [Fact]
    public void Filter_matches_field_names_exactly_and_leaves_out_fields_the_payload_does_not_have()
    {
        var details = SystemEventDetails.Filter(InventoryEvents.Imported, """{"ReferenceId":"A","itemcount":1,"totalQuantity":7}""");

        Assert.NotNull(details);
        Assert.Equal(["totalQuantity"], details.Select(p => p.Key));
    }

    [Theory]
    [InlineData("order.shipped", """{"orderId":"o-1"}""")] // no entry for the event type
    [InlineData("", """{"referenceId":"A"}""")]
    [InlineData(InventoryEvents.Imported, null)]
    [InlineData(InventoryEvents.Imported, "  ")]
    [InlineData(InventoryEvents.Imported, "\"text\"")]
    [InlineData(InventoryEvents.Imported, "[1,2]")]
    [InlineData(InventoryEvents.Imported, "null")]
    [InlineData(InventoryEvents.Imported, "{not json")]
    public void Filter_returns_nothing_for_an_unknown_event_type_or_a_payload_that_is_no_object(string eventType, string? payload)
    {
        Assert.Null(SystemEventDetails.Filter(eventType, payload));
    }

    [Fact]
    public void Filter_matches_the_event_type_exactly()
    {
        Assert.Null(SystemEventDetails.Filter("Inventory.Imported", """{"referenceId":"A"}"""));
        Assert.Null(SystemEventDetails.Filter("inventory.imported ", """{"referenceId":"A"}"""));
    }

    [Theory]
    [InlineData("actorUserId")]
    [InlineData("requestedBy")]
    [InlineData("acknowledgedBy")]
    public void Actor_is_read_from_each_actor_field_when_it_holds_a_user_id(string field)
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, SystemEventDetails.ActorIdOf(new JsonObject { [field] = id.ToString() }));
        Assert.Null(SystemEventDetails.TargetIdOf(new JsonObject { [field] = id.ToString() }));
    }

    [Fact]
    public void Target_is_read_from_the_target_field_only()
    {
        var (actor, target) = (Guid.NewGuid(), Guid.NewGuid());
        var details = new JsonObject { ["targetUserId"] = target.ToString(), ["actorUserId"] = actor.ToString() };

        Assert.Equal(target, SystemEventDetails.TargetIdOf(details));
        Assert.Equal(actor, SystemEventDetails.ActorIdOf(details));
    }

    [Fact]
    public void An_actor_that_is_no_user_id_is_nobody()
    {
        Assert.Null(SystemEventDetails.ActorIdOf(null));
        Assert.Null(SystemEventDetails.ActorIdOf(new JsonObject()));
        Assert.Null(SystemEventDetails.ActorIdOf(new JsonObject { ["requestedBy"] = "system" }));
        Assert.Null(SystemEventDetails.ActorIdOf(new JsonObject { ["actorUserId"] = "system:order-worker" }));
        Assert.Null(SystemEventDetails.ActorIdOf(new JsonObject { ["acknowledgedBy"] = null }));
        Assert.Null(SystemEventDetails.ActorIdOf(new JsonObject { ["actorUserId"] = 42 }));
        Assert.Null(SystemEventDetails.ActorIdOf(new JsonObject { ["actorUserId"] = new JsonObject() }));
        Assert.Null(SystemEventDetails.TargetIdOf(new JsonObject { ["targetUserId"] = "nobody" }));
    }

    [Fact]
    public void Every_actor_and_target_field_is_allowed_for_some_event_type()
    {
        // A field the lookup reads but no entry allows would never resolve anybody.
        var allowed = SystemEventDetails.AllowedFields.Values.SelectMany(fields => fields).ToHashSet();

        Assert.Subset(allowed, SystemEventDetails.ActorFields.Append(SystemEventDetails.TargetField).ToHashSet());
    }
}
