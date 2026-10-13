using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Dashboard;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Products;
using Omnichannel.Application.Realtime;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.Persistence.Alerts;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 017 Part C: <c>GET /api/alerts</c>, <c>GET /api/alerts/{id}</c>, <c>POST /api/alerts/{id}/acknowledge</c> and the
/// <c>alerts</c> section of the dashboard summary. Low-stock alerts come from the product and import endpoints; alerts of
/// the other types are written with the alert writer, as the workers do. Tests share one database: lists are narrowed to
/// this test's product or to a time window of its own.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AlertEndpointsTests(ApiFactory factory)
{
    private readonly HttpClient _staff = factory.CreateClientAs(AppRoles.Staff);
    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);

    [Fact]
    public async Task List_shows_what_needs_attention_with_names_and_hides_resolved_alerts_unless_asked_for()
    {
        var website = await ChannelAsync("website");
        var product = await CreateProductAsync(threshold: 5);
        var restocked = await CreateProductAsync(threshold: 5);
        await ImportAsync(restocked.ProductId, 10); // its low-stock alert is resolved
        var syncTask = ProcessingTask.ForStockSync(website.ChannelId, product.ProductId, "alert-list", null);
        var order = await SeedOrderAsync(website.ChannelId, syncTask);
        var syncFailed = await OpenAsync(AlertOccurrence.StockSyncFailed(website.ChannelId, product.ProductId, syncTask.TaskId, "channel_rejected"));
        var insufficient = await OpenAsync(AlertOccurrence.InsufficientStock(product.ProductId, order.OrderId));

        var page = await ListAsync($"productId={product.ProductId}");

        Assert.Equal((1, PageQuery.DefaultPageSize, 3), (page.Page, page.PageSize, page.TotalCount));
        // Most recent occurrence first.
        Assert.Equal([AlertType.InsufficientStock, AlertType.StockSyncFailed, AlertType.LowStock], page.Items.Select(a => a.Type));
        Assert.All(page.Items, a =>
        {
            Assert.Equal((AlertStatus.Open, (Guid?)product.ProductId, product.Sku, product.Name, 1), (a.Status, a.ProductId, a.ProductSku, a.ProductName, a.OccurrenceCount));
            Assert.Equal(((DateTimeOffset?)null, (string?)null, (DateTimeOffset?)null), (a.AcknowledgedAt, a.AcknowledgedByEmail, a.ResolvedAt));
            Assert.Equal(a.FirstOccurredAt, a.LastOccurredAt);
        });

        var (insufficientItem, syncItem, lowItem) = (page.Items[0], page.Items[1], page.Items[2]);
        Assert.Equal(
            (insufficient.AlertId, (Guid?)order.OrderId, (Guid?)null, (Guid?)null, (string?)null, (string?)null),
            (insufficientItem.AlertId, insufficientItem.OrderId, insufficientItem.TaskId, insufficientItem.ChannelId, insufficientItem.ChannelName, insufficientItem.ErrorCode));
        Assert.Equal(
            (syncFailed.AlertId, (Guid?)syncTask.TaskId, (Guid?)website.ChannelId, website.Name, "channel_rejected", (Guid?)null),
            (syncItem.AlertId, syncItem.TaskId, syncItem.ChannelId, syncItem.ChannelName, syncItem.ErrorCode, syncItem.OrderId));
        Assert.Equal(((Guid?)null, (Guid?)null, (Guid?)null, (string?)null), (lowItem.TaskId, lowItem.OrderId, lowItem.ChannelId, lowItem.ErrorCode));

        // The stock and the threshold of a low-stock alert; the other types carry none.
        Assert.Equal(((int?)0, (int?)5), (lowItem.AvailableStock, lowItem.LowStockThreshold));
        Assert.Equal(((int?)null, (int?)null), (syncItem.AvailableStock, syncItem.LowStockThreshold));
        Assert.Equal(((int?)null, (int?)null), (insufficientItem.AvailableStock, insufficientItem.LowStockThreshold));

        // Resolved alerts are hidden by default and listed when asked for.
        Assert.Empty((await ListAsync($"productId={restocked.ProductId}")).Items);
        var resolved = Assert.Single((await ListAsync($"productId={restocked.ProductId}&status=Resolved")).Items);
        Assert.Equal((AlertType.LowStock, AlertStatus.Resolved, true), (resolved.Type, resolved.Status, resolved.ResolvedAt is not null));
        Assert.Equal(((int?)10, (int?)5), (resolved.AvailableStock, resolved.LowStockThreshold));
    }

    [Fact]
    public async Task Low_stock_item_shows_the_stock_and_threshold_as_they_are_now()
    {
        var product = await CreateProductAsync(threshold: 5);
        var alert = Assert.Single((await ListAsync($"productId={product.ProductId}")).Items);
        Assert.Equal(((int?)0, (int?)5), (alert.AvailableStock, alert.LowStockThreshold));

        // Still low (3 <= 5, then 3 <= 4): the same alert, unchanged - only the joined numbers move.
        await ImportAsync(product.ProductId, 3);
        var update = await _manager.PutAsJsonAsync(
            $"/api/products/{product.ProductId}", new UpdateProductRequest(product.Name, product.Price, 4, null));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var now = Assert.Single((await ListAsync($"productId={product.ProductId}")).Items);
        Assert.Equal((alert.AlertId, AlertStatus.Open, 1, alert.LastOccurredAt), (now.AlertId, now.Status, now.OccurrenceCount, now.LastOccurredAt));
        Assert.Equal(((int?)3, (int?)4), (now.AvailableStock, now.LowStockThreshold));
        Assert.Equal(now, await GetAsync(alert.AlertId));
    }

    [Fact]
    public async Task Status_type_channel_and_product_filters_are_combined_and_status_and_type_are_repeatable()
    {
        var (website, pos) = (await ChannelAsync("website"), await ChannelAsync("pos"));
        var product = await CreateProductAsync(threshold: 5);
        var onWebsite = ProcessingTask.ForStockSync(website.ChannelId, product.ProductId, "alert-filter", null);
        var onPos = ProcessingTask.ForStockSync(pos.ChannelId, product.ProductId, "alert-filter", null);
        var order = await SeedOrderAsync(website.ChannelId, onWebsite, onPos);
        var websiteFailed = await OpenAsync(AlertOccurrence.StockSyncFailed(website.ChannelId, product.ProductId, onWebsite.TaskId, "channel_rejected"));
        var posFailed = await OpenAsync(AlertOccurrence.StockSyncFailed(pos.ChannelId, product.ProductId, onPos.TaskId, "channel_timeout"));
        var insufficient = await OpenAsync(AlertOccurrence.InsufficientStock(product.ProductId, order.OrderId));
        var lowStock = Assert.Single((await ListAsync($"productId={product.ProductId}&type=LowStock")).Items);
        await AcknowledgeAsync(_manager, posFailed.AlertId);
        await ResolveAsync(AlertDedupKey.InsufficientStock(product.ProductId));

        async Task<List<Guid>> IdsAsync(string filter) =>
            (await ListAsync($"productId={product.ProductId}&{filter}")).Items.Select(a => a.AlertId).Order().ToList();
        static List<Guid> Sorted(params Guid[] ids) => ids.Order().ToList();

        // Status: the default is Open + Acknowledged; each can be asked for, alone or together.
        Assert.Equal(Sorted(lowStock.AlertId, websiteFailed.AlertId, posFailed.AlertId), await IdsAsync("pageSize=50"));
        Assert.Equal(Sorted(lowStock.AlertId, websiteFailed.AlertId), await IdsAsync("status=Open"));
        Assert.Equal(Sorted(posFailed.AlertId), await IdsAsync("status=Acknowledged"));
        Assert.Equal(Sorted(insufficient.AlertId), await IdsAsync("status=Resolved"));
        Assert.Equal(Sorted(posFailed.AlertId, insufficient.AlertId), await IdsAsync("status=Acknowledged&status=Resolved"));
        Assert.Equal(
            Sorted(lowStock.AlertId, websiteFailed.AlertId, posFailed.AlertId, insufficient.AlertId),
            await IdsAsync("status=Open&status=Acknowledged&status=Resolved"));

        // Type, repeatable; combined with the status filter (default: unresolved).
        Assert.Equal(Sorted(websiteFailed.AlertId, posFailed.AlertId), await IdsAsync("type=StockSyncFailed"));
        Assert.Equal(Sorted(lowStock.AlertId, websiteFailed.AlertId, posFailed.AlertId), await IdsAsync("type=StockSyncFailed&type=LowStock"));
        Assert.Empty(await IdsAsync("type=InsufficientStock"));
        Assert.Equal(Sorted(insufficient.AlertId), await IdsAsync("type=InsufficientStock&status=Resolved"));
        Assert.Empty(await IdsAsync("type=ProcessingFailed"));

        // Channel.
        Assert.Equal(Sorted(websiteFailed.AlertId), await IdsAsync($"channelId={website.ChannelId}"));
        Assert.Equal(Sorted(posFailed.AlertId), await IdsAsync($"channelId={pos.ChannelId}&status=Acknowledged"));
        Assert.Empty(await IdsAsync($"channelId={pos.ChannelId}&status=Open"));

        // Another product: nothing of this one.
        Assert.Empty((await ListAsync($"productId={Guid.NewGuid()}")).Items);
    }

    [Fact]
    public async Task Range_on_the_last_occurrence_is_inclusive_and_sort_and_paging_follow_the_shared_contract()
    {
        // A window nobody else writes to: one hour of a year of its own per test run.
        var from = new DateTimeOffset(2003, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(Random.Shared.Next(1, 100_000));
        var to = from.AddHours(1);
        var tasks = Enumerable.Range(0, 5).Select(_ => new ProcessingTask(ProcessingTaskType.OrderProcessing, null, "alert-range", null)).ToList();
        await factory.QueryDbAsync(async db =>
        {
            db.ProcessingTasks.AddRange(tasks);
            return await db.SaveChangesAsync();
        });
        var alerts = new List<AlertChange>();
        foreach (var task in tasks)
        {
            alerts.Add(await OpenAsync(AlertOccurrence.ProcessingFailed(task.TaskId, null, "check_violation")));
        }

        // (last occurrence, first occurrence, count): the two ends of the range are part of it, a second outside is not.
        var (atStart, middle, atEnd, before, after) = (alerts[0].AlertId, alerts[1].AlertId, alerts[2].AlertId, alerts[3].AlertId, alerts[4].AlertId);
        await PlaceAsync(atStart, last: from, first: from.AddDays(-3), count: 7);
        await PlaceAsync(middle, last: from.AddMinutes(30), first: from.AddDays(-1), count: 2);
        await PlaceAsync(atEnd, last: to, first: from.AddDays(-2), count: 4);
        await PlaceAsync(before, last: from.AddSeconds(-1), first: from.AddDays(-9), count: 1);
        await PlaceAsync(after, last: to.AddSeconds(1), first: from.AddDays(-9), count: 1);
        var range = $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

        async Task<List<Guid>> IdsAsync(string query) => (await ListAsync(query)).Items.Select(a => a.AlertId).ToList();

        // Default order: most recent occurrence first.
        Assert.Equal([atEnd, middle, atStart], await IdsAsync(range));
        Assert.Equal([atStart, middle, atEnd], await IdsAsync($"{range}&sortDir=asc"));
        Assert.Equal([atEnd, middle, atStart], await IdsAsync($"{range}&sortBy=lastOccurredAt&sortDir=desc"));
        // The other sort fields, each descending unless asked otherwise.
        Assert.Equal([middle, atEnd, atStart], await IdsAsync($"{range}&sortBy=firstOccurredAt"));
        Assert.Equal([atStart, atEnd, middle], await IdsAsync($"{range}&sortBy=firstOccurredAt&sortDir=asc"));
        Assert.Equal([atStart, atEnd, middle], await IdsAsync($"{range}&sortBy=occurrenceCount"));
        Assert.Equal([middle, atEnd, atStart], await IdsAsync($"{range}&sortBy=OccurrenceCount&sortDir=ASC"));

        // One end alone.
        Assert.Equal([atEnd, middle, atStart, before], (await IdsAsync($"to={Uri.EscapeDataString(to.ToString("O"))}&type=ProcessingFailed&pageSize=100"))
            .Where(id => alerts.Any(a => a.AlertId == id)));
        // An offset other than UTC means the same instants.
        var plusSeven = TimeSpan.FromHours(7);
        var shifted = $"from={Uri.EscapeDataString(from.ToOffset(plusSeven).ToString("O"))}&to={Uri.EscapeDataString(to.ToOffset(plusSeven).ToString("O"))}";
        Assert.Equal([atEnd, middle, atStart], await IdsAsync(shifted));

        // Paging: the count is of the whole filter, the page is cut after sorting.
        var second = await ListAsync($"{range}&pageSize=2&page=2");
        Assert.Equal((2, 2, 3), (second.Page, second.PageSize, second.TotalCount));
        Assert.Equal([atStart], second.Items.Select(a => a.AlertId));
        var first = await ListAsync($"{range}&pageSize=2");
        Assert.Equal([atEnd, middle], first.Items.Select(a => a.AlertId));
        Assert.Empty((await ListAsync($"{range}&pageSize=2&page=3")).Items);
    }

    [Theory]
    [InlineData("status=Nope", "status")]
    [InlineData("type=Nope", "type")]
    // A number that is no status / type is refused by model binding as well (the validator's own check is never reached).
    [InlineData("status=Open&status=99", "status")]
    [InlineData("type=42", "type")]
    [InlineData("productId=not-a-guid", "productId")]
    [InlineData("channelId=not-a-guid", "channelId")]
    [InlineData("sortBy=status", "sortBy")]
    [InlineData("sortDir=sideways", "sortDir")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z", "to")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-04-04T00:00:00Z", "to")] // 93 days
    public async Task Invalid_query_is_a_400_on_the_camel_case_parameter(string query, string expectedKey)
    {
        var response = await _staff.GetAsync($"/api/alerts?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", document.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal([expectedKey], document.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Range_of_exactly_92_days_is_accepted_and_the_message_of_a_longer_one_follows_the_request_language()
    {
        Assert.Equal(HttpStatusCode.OK, (await _staff.GetAsync("/api/alerts?from=2026-01-01T00:00:00Z&to=2026-04-03T00:00:00Z")).StatusCode);

        async Task<string> MessageAsync(string language)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/alerts?from=2026-01-01T00:00:00Z&to=2026-04-04T00:00:00Z");
            request.Headers.AcceptLanguage.ParseAdd(language);
            var response = await _staff.SendAsync(request);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("errors").GetProperty("to")[0].GetString()!;
        }

        var english = await MessageAsync("en");
        var vietnamese = await MessageAsync("vi");
        Assert.Equal("The range from 'From date' to 'To date' must not be longer than 92 days.", english);
        Assert.NotEqual(english, vietnamese);
        Assert.Contains("92", vietnamese);
    }

    [Fact]
    public async Task Get_returns_the_item_of_the_list_and_404_for_an_unknown_alert()
    {
        var product = await CreateProductAsync(threshold: 2);
        var listed = Assert.Single((await ListAsync($"productId={product.ProductId}")).Items);

        var response = await _staff.GetAsync($"/api/alerts/{listed.AlertId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(listed, await response.ReadAsync<AlertResponse>());

        // The item carries exactly the documented members: no error message, payload or outbox data.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            new[]
            {
                "acknowledgedAt", "acknowledgedByEmail", "alertId", "availableStock", "channelId", "channelName", "errorCode", "firstOccurredAt",
                "lastOccurredAt", "lowStockThreshold", "occurrenceCount", "orderId", "productId", "productName", "productSku", "resolvedAt",
                "status", "taskId", "type"
            },
            document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(("LowStock", "Open"), (document.RootElement.GetProperty("type").GetString(), document.RootElement.GetProperty("status").GetString()));

        var unknown = await _staff.GetAsync($"/api/alerts/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(AlertErrorCodes.AlertNotFound, await unknown.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Manager_acknowledges_an_open_alert_once_with_audit_and_hint_and_acknowledging_again_changes_nothing()
    {
        await using var probe = await HintProbe.StartAsync(factory.DatabaseConnectionString);
        var product = await CreateProductAsync(threshold: 5);
        var open = Assert.Single((await ListAsync($"productId={product.ProductId}")).Items);

        var response = await _manager.PostAsync($"/api/alerts/{open.AlertId}/acknowledge", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var acknowledged = await response.ReadAsync<AlertResponse>();
        Assert.Equal(
            (open.AlertId, AlertStatus.Acknowledged, "manager@integration.test", true, (DateTimeOffset?)null),
            (acknowledged.AlertId, acknowledged.Status, acknowledged.AcknowledgedByEmail, acknowledged.AcknowledgedAt is not null, acknowledged.ResolvedAt));
        // Nothing else of the alert changed.
        Assert.Equal(open with { Status = AlertStatus.Acknowledged, AcknowledgedAt = acknowledged.AcknowledgedAt, AcknowledgedByEmail = acknowledged.AcknowledgedByEmail }, acknowledged);
        var stored = await factory.QueryDbAsync(db => db.Alerts.AsNoTracking().SingleAsync(a => a.AlertId == open.AlertId));
        Assert.Equal(factory.UserIdOf(AppRoles.Manager).ToString(), stored.AcknowledgedBy);

        // Again - by an Admin this time: 200 with the alert as it is, nothing written.
        var again = await factory.CreateClientAs(AppRoles.Admin).PostAsync($"/api/alerts/{open.AlertId}/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(acknowledged, await again.ReadAsync<AlertResponse>());

        // One audit event, of the manager.
        var audit = Assert.Single(await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .Where(l => l.EventType == AlertEvents.Acknowledged)
            .ToListAsync()), l => JsonNode.Parse(l.Payload!)!["alertId"]!.GetValue<Guid>() == open.AlertId);
        var payload = JsonNode.Parse(audit.Payload!)!;
        Assert.Equal(SystemEventLevel.Information, audit.Level);
        Assert.Equal(
            ("LowStock", factory.UserIdOf(AppRoles.Manager).ToString()),
            (payload["type"]!.GetValue<string>(), payload["acknowledgedBy"]!.GetValue<string>()));

        // The acknowledged alert still needs attention (default list) and is resolved by the system later, not by the user.
        Assert.Equal(open.AlertId, Assert.Single((await ListAsync($"productId={product.ProductId}")).Items).AlertId);
        await ImportAsync(product.ProductId, 10);
        var resolved = await GetAsync(open.AlertId);
        Assert.Equal((AlertStatus.Resolved, acknowledged.AcknowledgedAt, "manager@integration.test"), (resolved.Status, resolved.AcknowledgedAt, resolved.AcknowledgedByEmail));

        // Hints of the alert: opened (two), acknowledged once (the second acknowledge sent nothing), resolved.
        RealtimeHint Changed(AlertStatus status) => RealtimeHint.AlertChanged(open.AlertId, AlertType.LowStock, status, product.ProductId, null);
        RealtimeHint[] expected =
        [
            Changed(AlertStatus.Open), RealtimeHint.LowStockCreated(open.AlertId, product.ProductId), Changed(AlertStatus.Acknowledged), Changed(AlertStatus.Resolved)
        ];
        await probe.WaitForAsync(all => all.SelectMany(n => n.Events).Count(e => e.AlertId == open.AlertId) >= expected.Length, "hints of the alert");
        Assert.Equal(expected, probe.Events.Where(e => e.AlertId == open.AlertId));
    }

    [Fact]
    public async Task Acknowledge_is_refused_for_a_resolved_alert_for_staff_for_anonymous_and_for_an_unknown_alert()
    {
        var product = await CreateProductAsync(threshold: 5);
        var open = Assert.Single((await ListAsync($"productId={product.ProductId}")).Items);

        // Staff may read alerts, not acknowledge them; nobody may without a token.
        var asStaff = await _staff.PostAsync($"/api/alerts/{open.AlertId}/acknowledge", null);
        var anonymous = await factory.CreateAnonymousClient().PostAsync($"/api/alerts/{open.AlertId}/acknowledge", null);
        Assert.Equal(HttpStatusCode.Forbidden, asStaff.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateAnonymousClient().GetAsync("/api/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateAnonymousClient().GetAsync($"/api/alerts/{open.AlertId}")).StatusCode);
        Assert.Equal(AlertStatus.Open, (await GetAsync(open.AlertId)).Status);

        // Resolved by the system: there is nothing left to acknowledge.
        await ImportAsync(product.ProductId, 10);
        var resolved = await _manager.PostAsync($"/api/alerts/{open.AlertId}/acknowledge", null);
        Assert.Equal(HttpStatusCode.Conflict, resolved.StatusCode);
        Assert.Equal(AlertErrorCodes.AlertResolved, await resolved.ReadErrorCodeAsync());
        var after = await GetAsync(open.AlertId);
        Assert.Equal((AlertStatus.Resolved, (DateTimeOffset?)null, (string?)null), (after.Status, after.AcknowledgedAt, after.AcknowledgedByEmail));

        var unknown = await _manager.PostAsync($"/api/alerts/{Guid.NewGuid()}/acknowledge", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(AlertErrorCodes.AlertNotFound, await unknown.ReadErrorCodeAsync());

        // Nothing was acknowledged, so nothing was audited.
        var audited = await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .Where(l => l.EventType == AlertEvents.Acknowledged)
            .Select(l => l.Payload!)
            .ToListAsync());
        Assert.DoesNotContain(audited, payload => JsonNode.Parse(payload)!["alertId"]!.GetValue<Guid>() == open.AlertId);
    }

    [Fact]
    public async Task Summary_counts_unresolved_alerts_by_status_and_type_with_every_type_listed()
    {
        // Independent counts of what is unresolved now (the collection runs its tests one after the other).
        async Task<Dictionary<(AlertType, AlertStatus), int>> CountsAsync() =>
            (await factory.QueryDbAsync(db => db.Alerts.AsNoTracking()
                .Where(a => a.Status != AlertStatus.Resolved)
                .Select(a => new { a.Type, a.Status })
                .ToListAsync()))
            .GroupBy(a => (a.Type, a.Status))
            .ToDictionary(g => g.Key, g => g.Count());

        async Task<DashboardAlerts> SummaryAsync()
        {
            var response = await _staff.GetAsync("/api/dashboard/summary");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.ReadAsync<DashboardSummaryResponse>()).Alerts;
        }

        var before = await SummaryAsync();
        var counts = await CountsAsync();

        // Every type, in the order of the enum, zeros included; the totals are the sums.
        Assert.Equal(Enum.GetValues<AlertType>(), before.ByType.Select(t => t.Type));
        Assert.All(before.ByType, t => Assert.Equal(
            (counts.GetValueOrDefault((t.Type, AlertStatus.Open)), counts.GetValueOrDefault((t.Type, AlertStatus.Acknowledged))),
            (t.Open, t.Acknowledged)));
        Assert.Equal((before.ByType.Sum(t => t.Open), before.ByType.Sum(t => t.Acknowledged)), (before.Open, before.Acknowledged));

        DashboardAlertTypeCount Of(DashboardAlerts alerts, AlertType type) => alerts.ByType.Single(t => t.Type == type);

        // A new low-stock alert is counted as open ...
        var product = await CreateProductAsync(threshold: 5);
        var alert = Assert.Single((await ListAsync($"productId={product.ProductId}")).Items);
        var opened = await SummaryAsync();
        Assert.Equal((before.Open + 1, before.Acknowledged), (opened.Open, opened.Acknowledged));
        Assert.Equal(
            (Of(before, AlertType.LowStock).Open + 1, Of(before, AlertType.LowStock).Acknowledged),
            (Of(opened, AlertType.LowStock).Open, Of(opened, AlertType.LowStock).Acknowledged));
        Assert.Equal(Of(before, AlertType.StockSyncFailed), Of(opened, AlertType.StockSyncFailed));

        // ... as acknowledged once a manager has seen it ...
        await AcknowledgeAsync(_manager, alert.AlertId);
        var acknowledged = await SummaryAsync();
        Assert.Equal((before.Open, before.Acknowledged + 1), (acknowledged.Open, acknowledged.Acknowledged));
        Assert.Equal(Of(before, AlertType.LowStock).Acknowledged + 1, Of(acknowledged, AlertType.LowStock).Acknowledged);

        // ... and not at all once it is resolved. The range of the summary does not apply to alerts.
        await ImportAsync(product.ProductId, 10);
        var resolved = await SummaryAsync();
        Assert.Equal((before.Open, before.Acknowledged), (resolved.Open, resolved.Acknowledged));
        var otherRange = await (await _staff.GetAsync("/api/dashboard/summary?from=2001-01-01T00:00:00Z&to=2001-01-02T00:00:00Z"))
            .ReadAsync<DashboardSummaryResponse>();
        Assert.Equal((resolved.Open, resolved.Acknowledged), (otherRange.Alerts.Open, otherRange.Alerts.Acknowledged));
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    /// <summary>Task 026b B1: the e-mail addresses of a page come from one lookup of its user ids, with the answer unchanged.</summary>
    [Fact]
    public async Task A_page_of_alerts_acknowledged_by_users_and_other_actors_carries_each_users_email_and_null_for_the_rest()
    {
        var manager = factory.UserIdOf(AppRoles.Manager).ToString();
        var gone = Guid.NewGuid().ToString();
        string?[] actors = [manager, factory.UserIdOf(AppRoles.Admin).ToString(), "system", "webhook:website", gone];
        string?[] expected = ["manager@integration.test", "admin@integration.test", null, null, null];
        var alertIds = new List<Guid>();
        foreach (var actor in actors)
        {
            var product = await CreateProductAsync(threshold: 5);
            var open = Assert.Single((await ListAsync($"productId={product.ProductId}")).Items);
            Assert.Equal(HttpStatusCode.OK, (await _manager.PostAsync($"/api/alerts/{open.AlertId}/acknowledge", null)).StatusCode);
            await factory.QueryDbAsync(db => db.Database.ExecuteSqlAsync(
                $"UPDATE \"Alerts\" SET \"AcknowledgedBy\" = {actor} WHERE \"AlertId\" = {open.AlertId}"));
            alertIds.Add(open.AlertId);
        }

        // One page that holds all five (the newest alerts of this type and status come first), and each one alone.
        var page = (await ListAsync("type=LowStock&status=Acknowledged&pageSize=100")).Items.ToDictionary(a => a.AlertId);
        for (var n = 0; n < alertIds.Count; n++)
        {
            Assert.Equal(expected[n], page[alertIds[n]].AcknowledgedByEmail);
            Assert.Equal(page[alertIds[n]], await GetAsync(alertIds[n]));
        }
    }

    private async Task<PagedResult<AlertResponse>> ListAsync(string query)
    {
        var response = await _staff.GetAsync($"/api/alerts?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<PagedResult<AlertResponse>>();
    }

    private async Task<AlertResponse> GetAsync(Guid alertId)
    {
        var response = await _staff.GetAsync($"/api/alerts/{alertId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<AlertResponse>();
    }

    private static async Task AcknowledgeAsync(HttpClient client, Guid alertId) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/alerts/{alertId}/acknowledge", null)).StatusCode);

    /// <summary>A product through the API: it has no stock, so it starts with a low-stock alert.</summary>
    private async Task<ProductResponse> CreateProductAsync(int threshold)
    {
        var response = await _manager.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest($"ALERT-{TestData.UniqueSuffix()}".ToUpperInvariant(), $"Alert product {TestData.UniqueSuffix()}", 12.5m, threshold, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<ProductResponse>();
    }

    private async Task ImportAsync(Guid productId, int stock)
    {
        var response = await _manager.PostAsJsonAsync(
            "/api/inventory/import",
            new ImportInventoryRequest($"GRN-{TestData.UniqueSuffix()}", null, [new ImportInventoryItem(productId, stock)]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<Channel> ChannelAsync(string slug) =>
        factory.QueryDbAsync(db => db.Channels.AsNoTracking().SingleAsync(c => c.Slug == slug));

    /// <summary>Stores the tasks and an order of a new customer on the channel (what failure alerts refer to).</summary>
    private async Task<Order> SeedOrderAsync(Guid channelId, params ProcessingTask[] tasks)
    {
        var customer = new Customer($"Alert customer {TestData.UniqueSuffix()}", null, null, null);
        var order = new Order(channelId, customer.CustomerId, $"ALERT-{Guid.NewGuid():N}", OrderStatus.InsufficientStock);
        await factory.QueryDbAsync(async db =>
        {
            db.Customers.Add(customer);
            db.Orders.Add(order);
            db.ProcessingTasks.AddRange(tasks);
            return await db.SaveChangesAsync();
        });
        return order;
    }

    /// <summary>Opens (or refreshes) an alert as a worker does: with the alert writer, inside a transaction.</summary>
    private async Task<AlertChange> OpenAsync(AlertOccurrence occurrence)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var change = Assert.Single(await scope.ServiceProvider.GetRequiredService<AlertStore>().OpenOrRefreshAsync([occurrence], CancellationToken.None));
        await transaction.CommitAsync();
        return change;
    }

    private async Task ResolveAsync(string dedupKey)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<AlertStore>().ResolveAsync([dedupKey], CancellationToken.None));
        await transaction.CommitAsync();
    }

    /// <summary>Moves an alert in time (and sets its count), as only a test can.</summary>
    private Task PlaceAsync(Guid alertId, DateTimeOffset last, DateTimeOffset first, int count) =>
        factory.QueryDbAsync(db => db.Alerts.Where(a => a.AlertId == alertId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.LastOccurredAt, last)
            .SetProperty(a => a.FirstOccurredAt, first)
            .SetProperty(a => a.OccurrenceCount, count)));
}
