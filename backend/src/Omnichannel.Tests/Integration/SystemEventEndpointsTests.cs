using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Products;
using Omnichannel.Application.SystemEvents;
using Omnichannel.Application.Users;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 018b Part B: <c>GET /api/system-events</c> (Logs/Audit). Events are stored directly, as the services and workers
/// write them; three tests let the API write real ones. Tests share one database: every list is narrowed to a correlation
/// id or a time window of its own.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SystemEventEndpointsTests(ApiFactory factory)
{
    private const string CorrelationHeader = "X-Correlation-Id";

    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _admin = factory.CreateClientAs(AppRoles.Admin);
    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);
    private readonly HttpClient _staff = factory.CreateClientAs(AppRoles.Staff);

    [Fact]
    public async Task List_is_newest_first_with_the_identity_as_tie_break_and_follows_the_paging_contract()
    {
        var correlationId = NewCorrelationId();
        // Two events share a time: the one stored later (the higher identity) counts as the newer one.
        var a = await SeedAsync("test.order.a", correlationId, T0);
        var b = await SeedAsync("test.order.b", correlationId, T0);
        var c = await SeedAsync("test.order.c", correlationId, T0.AddSeconds(1));
        var d = await SeedAsync("test.order.d", correlationId, T0.AddSeconds(2));
        var e = await SeedAsync("test.order.e", correlationId, T0.AddSeconds(3));

        var all = await ListAsync(_manager, $"correlationId={correlationId}");
        Assert.Equal((1, PageQuery.DefaultPageSize, 5), (all.Page, all.PageSize, all.TotalCount));
        Assert.Equal([e.LogId, d.LogId, c.LogId, b.LogId, a.LogId], Ids(all));

        var first = await ListAsync(_manager, $"correlationId={correlationId}&pageSize=2");
        Assert.Equal((1, 2, 5), (first.Page, first.PageSize, first.TotalCount));
        Assert.Equal([e.LogId, d.LogId], Ids(first));
        Assert.Equal([c.LogId, b.LogId], Ids(await ListAsync(_manager, $"correlationId={correlationId}&pageSize=2&page=2")));
        Assert.Equal([a.LogId], Ids(await ListAsync(_manager, $"correlationId={correlationId}&pageSize=2&page=3")));
        var beyond = await ListAsync(_manager, $"correlationId={correlationId}&pageSize=2&page=4");
        Assert.Equal((4, 5), (beyond.Page, beyond.TotalCount));
        Assert.Empty(beyond.Items);

        // The only sort field is the creation time; ascending turns the tie-break around as well.
        Assert.Equal(
            [a.LogId, b.LogId, c.LogId, d.LogId, e.LogId],
            Ids(await ListAsync(_manager, $"correlationId={correlationId}&sortDir=asc")));
        Assert.Equal(
            [a.LogId, b.LogId],
            Ids(await ListAsync(_manager, $"correlationId={correlationId}&sortBy=createdAt&sortDir=ASC&pageSize=2")));
        Assert.Equal(
            [e.LogId, d.LogId, c.LogId, b.LogId, a.LogId],
            Ids(await ListAsync(_manager, $"correlationId={correlationId}&sortBy=createdAt&sortDir=desc")));
    }

    [Fact]
    public async Task Without_a_filter_the_newest_events_of_the_whole_table_come_first()
    {
        var correlationId = NewCorrelationId();
        await SeedAsync(InventoryEvents.Imported, correlationId, DateTimeOffset.UtcNow.AddSeconds(-1));
        await SeedAsync(UserEvents.Locked, correlationId, DateTimeOffset.UtcNow);

        var page = await ListAsync(_admin, "pageSize=50");

        var expected = await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.LogId)
            .Select(e => e.LogId)
            .Take(50)
            .ToListAsync());
        Assert.Equal(expected, Ids(page));
        Assert.Equal(await factory.QueryDbAsync(db => db.SystemEventLogs.CountAsync()), page.TotalCount);
        Assert.Equal(correlationId, page.Items[0].CorrelationId);
    }

    [Fact]
    public async Task Item_carries_the_columns_of_the_event_and_nothing_of_the_payload_outside_details()
    {
        var correlationId = NewCorrelationId();
        var (taskId, orderId) = (Guid.NewGuid(), Guid.NewGuid());
        var stored = await SeedAsync(
            ProcessingEvents.Failed,
            correlationId,
            T0,
            new { taskId, taskType = "OrderProcessing", attemptNo = 5, errorCode = "timeout", errorMessage = "The operation timed out.", queue = "order.processing" },
            SystemEventLevel.Error,
            taskId,
            orderId,
            "OrderWorker");

        var response = await _manager.GetAsync($"/api/system-events?correlationId={correlationId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            new[]
            {
                "actorEmail", "correlationId", "createdAt", "details", "eventType", "level", "logId", "orderId", "targetEmail", "taskId",
                "workerName"
            },
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            (stored.LogId, "Error", ProcessingEvents.Failed, correlationId, taskId, orderId, "OrderWorker"),
            (item.GetProperty("logId").GetInt64(), item.GetProperty("level").GetString(), item.GetProperty("eventType").GetString(),
                item.GetProperty("correlationId").GetString(), item.GetProperty("taskId").GetGuid(), item.GetProperty("orderId").GetGuid(),
                item.GetProperty("workerName").GetString()));
        Assert.Equal(T0, item.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("actorEmail").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("targetEmail").ValueKind);
        Assert.Equal(
            ["taskId", "taskType", "attemptNo", "errorCode", "errorMessage", "queue"],
            item.GetProperty("details").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Level_event_type_correlation_order_and_task_filters_are_combined_and_level_and_event_type_are_repeatable()
    {
        var (correlationId, otherCorrelationId) = (NewCorrelationId(), NewCorrelationId());
        var (orderId, taskId) = (Guid.NewGuid(), Guid.NewGuid());
        var info = await SeedAsync("test.filter.one", correlationId, T0, orderId: orderId);
        var warning = await SeedAsync("test.filter.two", correlationId, T0.AddSeconds(1), level: SystemEventLevel.Warning, taskId: taskId);
        var error = await SeedAsync("test.filter.two", correlationId, T0.AddSeconds(2), level: SystemEventLevel.Error, taskId: taskId, orderId: orderId);
        var critical = await SeedAsync("test.filter.three", correlationId, T0.AddSeconds(3), level: SystemEventLevel.Critical);
        var elsewhere = await SeedAsync("test.filter.one", otherCorrelationId, T0, level: SystemEventLevel.Error, taskId: taskId, orderId: orderId);

        async Task<List<long>> IdsAsync(string filter) => Ids(await ListAsync(_manager, filter));
        var own = $"correlationId={correlationId}";

        // Correlation id: exact.
        Assert.Equal([critical.LogId, error.LogId, warning.LogId, info.LogId], await IdsAsync(own));
        Assert.Equal([elsewhere.LogId], await IdsAsync($"correlationId={otherCorrelationId}"));
        Assert.Empty(await IdsAsync($"correlationId={correlationId[..^1]}"));
        Assert.Empty(await IdsAsync($"correlationId={correlationId.ToUpperInvariant()}"));

        // Level, repeatable.
        Assert.Equal([error.LogId], await IdsAsync($"{own}&level=Error"));
        Assert.Equal([error.LogId, warning.LogId], await IdsAsync($"{own}&level=Error&level=Warning"));
        Assert.Equal([critical.LogId, info.LogId], await IdsAsync($"{own}&level=Information&level=Critical"));
        Assert.Equal([critical.LogId, error.LogId, warning.LogId, info.LogId], await IdsAsync($"{own}&level=Information&level=Warning&level=Error&level=Critical"));

        // Event type, repeatable and exact (no prefix, no other casing).
        Assert.Equal([error.LogId, warning.LogId], await IdsAsync($"{own}&eventType=test.filter.two"));
        Assert.Equal([critical.LogId, info.LogId], await IdsAsync($"{own}&eventType=test.filter.one&eventType=test.filter.three"));
        Assert.Empty(await IdsAsync($"{own}&eventType=test.filter"));
        Assert.Empty(await IdsAsync($"{own}&eventType=TEST.FILTER.ONE"));

        // Order and task: on their own they reach across correlation ids.
        Assert.Equal([error.LogId, info.LogId], await IdsAsync($"{own}&orderId={orderId}"));
        Assert.Equal([error.LogId, elsewhere.LogId, info.LogId], await IdsAsync($"orderId={orderId}"));
        Assert.Equal([error.LogId, warning.LogId], await IdsAsync($"{own}&taskId={taskId}"));
        Assert.Equal([error.LogId, warning.LogId, elsewhere.LogId], await IdsAsync($"taskId={taskId}"));
        Assert.Empty(await IdsAsync($"{own}&orderId={Guid.NewGuid()}"));
        Assert.Empty(await IdsAsync($"{own}&taskId={Guid.NewGuid()}"));

        // All together.
        Assert.Equal([error.LogId], await IdsAsync($"{own}&level=Error&level=Critical&eventType=test.filter.two&orderId={orderId}&taskId={taskId}"));
        Assert.Empty(await IdsAsync($"{own}&level=Critical&eventType=test.filter.two"));

        // "search" is not a filter of this list.
        Assert.Equal(4, (await IdsAsync($"{own}&search=nothing-matches-this")).Count);
    }

    [Fact]
    public async Task Range_on_the_creation_time_is_inclusive_and_takes_any_offset()
    {
        // A window nobody else writes to: one hour of a year of its own per test run.
        var from = new DateTimeOffset(2004, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(Random.Shared.Next(1, 100_000));
        var to = from.AddHours(1);
        var correlationId = NewCorrelationId();
        var before = await SeedAsync("test.range", correlationId, from.AddSeconds(-1));
        var atFrom = await SeedAsync("test.range", correlationId, from);
        var inside = await SeedAsync("test.range", correlationId, from.AddMinutes(30));
        var atTo = await SeedAsync("test.range", correlationId, to);
        var after = await SeedAsync("test.range", correlationId, to.AddSeconds(1));

        async Task<List<long>> IdsAsync(string filter) => Ids(await ListAsync(_manager, filter));
        var own = $"correlationId={correlationId}";

        // Both ends are part of the range; the range alone is a filter.
        Assert.Equal([atTo.LogId, inside.LogId, atFrom.LogId], await IdsAsync($"from={Iso(from)}&to={Iso(to)}"));
        Assert.Equal([after.LogId, atTo.LogId, inside.LogId, atFrom.LogId], await IdsAsync($"{own}&from={Iso(from)}"));
        Assert.Equal([atTo.LogId, inside.LogId, atFrom.LogId, before.LogId], await IdsAsync($"{own}&to={Iso(to)}"));
        Assert.Equal([inside.LogId], await IdsAsync($"from={Iso(from.AddSeconds(1))}&to={Iso(to.AddSeconds(-1))}"));
        Assert.Equal([atFrom.LogId], await IdsAsync($"from={Iso(from)}&to={Iso(from)}"));

        // The same instants written with an offset, as the page sends them.
        var offset = TimeSpan.FromHours(7);
        Assert.Equal(
            [atTo.LogId, inside.LogId, atFrom.LogId],
            await IdsAsync($"from={Iso(from.ToOffset(offset))}&to={Iso(to.ToOffset(offset))}"));
    }

    [Theory]
    [InlineData("level=Fatal", "level")]
    // A number that is no level is refused by model binding as well.
    [InlineData("level=Error&level=99", "level")]
    [InlineData("orderId=not-a-guid", "orderId")]
    [InlineData("taskId=not-a-guid", "taskId")]
    [InlineData("sortBy=level", "sortBy")]
    [InlineData("sortBy=eventType", "sortBy")]
    [InlineData("sortDir=sideways", "sortDir")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("page=0", "page")]
    [InlineData("from=yesterday", "from")]
    [InlineData("from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z", "to")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-04-04T00:00:00Z", "to")] // 93 days
    public async Task Invalid_query_is_a_400_on_the_camel_case_parameter(string query, string expectedKey)
    {
        var response = await _manager.GetAsync($"/api/system-events?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ReadErrorCodeAsync());
        Assert.Equal([expectedKey], await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task Text_filters_longer_than_their_columns_are_a_400()
    {
        var tooLong = new string('x', 101);

        var correlation = await _manager.GetAsync($"/api/system-events?correlationId={tooLong}");
        Assert.Equal(HttpStatusCode.BadRequest, correlation.StatusCode);
        Assert.Equal(["correlationId"], await ErrorKeysAsync(correlation));

        var eventType = await _manager.GetAsync($"/api/system-events?eventType=inventory.imported&eventType={tooLong}");
        Assert.Equal(HttpStatusCode.BadRequest, eventType.StatusCode);
        Assert.Equal(["eventType[1]"], await ErrorKeysAsync(eventType));

        Assert.Equal(HttpStatusCode.OK, (await _manager.GetAsync($"/api/system-events?correlationId={tooLong[..100]}&eventType={tooLong[..100]}")).StatusCode);
    }

    [Fact]
    public async Task Range_of_exactly_92_days_is_accepted_and_the_message_of_a_longer_one_follows_the_request_language()
    {
        Assert.Equal(
            HttpStatusCode.OK,
            (await _manager.GetAsync("/api/system-events?from=2026-01-01T00:00:00Z&to=2026-04-03T00:00:00Z")).StatusCode);

        async Task<string> MessageAsync(string query, string language)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/system-events?{query}");
            request.Headers.AcceptLanguage.ParseAdd(language);
            var response = await _manager.SendAsync(request);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("errors").GetProperty("to")[0].GetString()!;
        }

        const string tooLong = "from=2026-01-01T00:00:00Z&to=2026-04-04T00:00:00Z";
        Assert.Equal("The range from 'From date' to 'To date' must not be longer than 92 days.", await MessageAsync(tooLong, "en"));
        Assert.Equal("Khoảng thời gian từ 'Từ ngày' đến 'Đến ngày' không được dài quá 92 ngày.", await MessageAsync(tooLong, "vi"));

        // The same messages as the processing list for a range that ends before it starts.
        const string inverted = "from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z";
        Assert.Equal("'To date' must not be earlier than 'From date'.", await MessageAsync(inverted, "en"));
        Assert.Equal("'Đến ngày' không được sớm hơn 'Từ ngày'.", await MessageAsync(inverted, "vi"));

        // Only one end: no limit to check.
        Assert.Equal(HttpStatusCode.OK, (await _manager.GetAsync("/api/system-events?from=2020-01-01T00:00:00Z&pageSize=1")).StatusCode);
    }

    [Fact]
    public async Task User_events_exist_for_an_admin_only_whatever_the_filter()
    {
        var correlationId = NewCorrelationId();
        var imported = await SeedAsync(InventoryEvents.Imported, correlationId, T0);
        var created = await SeedAsync(UserEvents.Created, correlationId, T0.AddSeconds(1));
        var locked = await SeedAsync(UserEvents.Locked, correlationId, T0.AddSeconds(2));
        // Not a user management event: the rule is the prefix "user.", not "user".
        var lookalike = await SeedAsync("users.exported", correlationId, T0.AddSeconds(3));
        var own = $"correlationId={correlationId}";

        var forManager = await ListAsync(_manager, own);
        Assert.Equal([lookalike.LogId, imported.LogId], Ids(forManager));
        Assert.Equal(2, forManager.TotalCount);

        // Asked for by name: an empty page, not an error - and not a hint that such events exist.
        var asked = await ListAsync(_manager, $"{own}&eventType={UserEvents.Created}");
        Assert.Empty(asked.Items);
        Assert.Equal(0, asked.TotalCount);
        Assert.Equal([imported.LogId], Ids(await ListAsync(_manager, $"{own}&eventType={UserEvents.Locked}&eventType={InventoryEvents.Imported}")));
        Assert.Empty((await ListAsync(_manager, $"{own}&level=Information&eventType={UserEvents.Locked}&sortDir=asc")).Items);

        var forAdmin = await ListAsync(_admin, own);
        Assert.Equal([lookalike.LogId, locked.LogId, created.LogId, imported.LogId], Ids(forAdmin));
        Assert.Equal(4, forAdmin.TotalCount);
        Assert.Equal([created.LogId], Ids(await ListAsync(_admin, $"{own}&eventType={UserEvents.Created}")));
    }

    [Fact]
    public async Task Staff_is_forbidden_and_an_anonymous_caller_unauthorized()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _staff.GetAsync("/api/system-events")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _staff.GetAsync($"/api/system-events?eventType={InventoryEvents.Imported}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/system-events")).StatusCode);
    }

    /// <summary>
    /// One stored event per entry of the allowlist, shaped like the code that writes it, each with something that must not
    /// come back: an extra field, the reason of a rejected message, the outbox error.
    /// </summary>
    public static TheoryData<string, string, string> DetailCases() => new()
    {
        {
            InventoryEvents.Imported,
            """{"referenceId":"PN/2026-0042","itemCount":2,"totalQuantity":150,"extra":"dropped"}""",
            """{"referenceId":"PN/2026-0042","itemCount":2,"totalQuantity":150}"""
        },
        {
            OrderProcessingEvents.InsufficientStock,
            """{"orderId":"01a10000-0000-7000-8000-000000000001","taskId":"01a10000-0000-7000-8000-000000000002","items":[{"productId":"01a10000-0000-7000-8000-000000000003","requested":100,"available":4}],"extra":"dropped"}""",
            """{"orderId":"01a10000-0000-7000-8000-000000000001","taskId":"01a10000-0000-7000-8000-000000000002","items":[{"productId":"01a10000-0000-7000-8000-000000000003","requested":100,"available":4}]}"""
        },
        {
            OrderProcessingEvents.OrderAlreadyExists,
            """{"taskId":"01a10000-0000-7000-8000-000000000002","orderId":"01a10000-0000-7000-8000-000000000001","externalOrderId":"WEB-20261003-AB12CD","extra":"dropped"}""",
            """{"taskId":"01a10000-0000-7000-8000-000000000002","orderId":"01a10000-0000-7000-8000-000000000001","externalOrderId":"WEB-20261003-AB12CD"}"""
        },
        {
            OrderProcessingEvents.MessageRejected,
            """{"messageId":"m-1","reason":"Body is not a valid message envelope: 'p' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0."}""",
            """{"messageId":"m-1"}"""
        },
        {
            StockSyncEvents.MessageRejected,
            """{"messageId":"m-2","reason":"Unexpected event type 'DROP TABLE'; expected 'inventory.changed' or 'stock.sync.requested'.","queue":"stock.sync.website"}""",
            """{"messageId":"m-2","queue":"stock.sync.website"}"""
        },
        {
            ProcessingEvents.Failed,
            """{"taskId":"01a10000-0000-7000-8000-000000000002","taskType":"StockSync","attemptNo":3,"errorCode":"channel_rejected","errorMessage":"The channel rejected the product.","queue":"stock.sync.website","extra":"dropped"}""",
            """{"taskId":"01a10000-0000-7000-8000-000000000002","taskType":"StockSync","attemptNo":3,"errorCode":"channel_rejected","errorMessage":"The channel rejected the product.","queue":"stock.sync.website"}"""
        },
        {
            ProcessingEvents.RetryRequested,
            """{"taskId":"01a10000-0000-7000-8000-000000000002","taskType":"OrderProcessing","requestedBy":"system","previousMessageId":"m-3","newMessageId":"m-4","attemptBudgetStart":5,"extra":"dropped"}""",
            """{"taskId":"01a10000-0000-7000-8000-000000000002","taskType":"OrderProcessing","requestedBy":"system","previousMessageId":"m-3","newMessageId":"m-4","attemptBudgetStart":5}"""
        },
        {
            OutboxEvents.PublishFailed,
            """{"outboxEventId":"01a10000-0000-7000-8000-000000000005","eventType":"order.created","publishAttempts":10,"lastError":"NO_ROUTE amqp://omni:secret@broker"}""",
            """{"outboxEventId":"01a10000-0000-7000-8000-000000000005","eventType":"order.created","publishAttempts":10}"""
        },
        {
            AlertEvents.Acknowledged,
            """{"alertId":"01a10000-0000-7000-8000-000000000006","type":"LowStock","acknowledgedBy":"system","extra":"dropped"}""",
            """{"alertId":"01a10000-0000-7000-8000-000000000006","type":"LowStock","acknowledgedBy":"system"}"""
        },
        {
            UserEvents.Created,
            """{"targetUserId":"01a10000-0000-7000-8000-000000000007","actorUserId":"system","role":"Manager","password":"dropped"}""",
            """{"targetUserId":"01a10000-0000-7000-8000-000000000007","actorUserId":"system","role":"Manager"}"""
        },
        {
            UserEvents.RoleChanged,
            """{"targetUserId":"01a10000-0000-7000-8000-000000000007","actorUserId":"system","from":"Staff","to":"Manager","extra":"dropped"}""",
            """{"targetUserId":"01a10000-0000-7000-8000-000000000007","actorUserId":"system","from":"Staff","to":"Manager"}"""
        },
        {
            UserEvents.Locked,
            """{"targetUserId":"01a10000-0000-7000-8000-000000000007","actorUserId":"system","extra":"dropped"}""",
            """{"targetUserId":"01a10000-0000-7000-8000-000000000007","actorUserId":"system"}"""
        },
        {
            UserEvents.Unlocked,
            """{"targetUserId":"01a10000-0000-7000-8000-000000000007","actorUserId":"system","extra":"dropped"}""",
            """{"targetUserId":"01a10000-0000-7000-8000-000000000007","actorUserId":"system"}"""
        },
        {
            UserEvents.PasswordReset,
            """{"targetUserId":"01a10000-0000-7000-8000-000000000007","actorUserId":"system","newPassword":"dropped"}""",
            """{"targetUserId":"01a10000-0000-7000-8000-000000000007","actorUserId":"system"}"""
        }
    };

    [Fact]
    public void Detail_cases_cover_every_entry_of_the_allowlist()
    {
        Assert.Equal(
            SystemEventDetails.AllowedFields.Keys.Order(StringComparer.Ordinal),
            DetailCases().Select(row => (string)row[0]).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(DetailCases))]
    public async Task Details_are_the_allowed_fields_of_the_payload_and_nothing_else(string eventType, string payload, string expectedDetails)
    {
        var correlationId = NewCorrelationId();
        await SeedRawAsync(eventType, correlationId, T0, payload);

        var response = await _admin.GetAsync($"/api/system-events?correlationId={correlationId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var item = Assert.Single(JsonSerializer.Deserialize<PagedResult<SystemEventResponse>>(body, TestData.Json)!.Items);
        Assert.Equal(eventType, item.EventType);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedDetails), item.Details), $"details: {item.Details?.ToJsonString()}");
        // Nowhere in the answer, under any name.
        Assert.DoesNotContain("dropped", body);
        Assert.DoesNotContain("lastError", body);
        Assert.DoesNotContain("reason", body);
        Assert.DoesNotContain("secret", body);
    }

    [Fact]
    public async Task Unknown_event_type_and_a_payload_that_is_no_object_have_no_details()
    {
        var correlationId = NewCorrelationId();
        var unknown = await SeedRawAsync("order.shipped", correlationId, T0, """{"orderId":"o-1","actorUserId":"x"}""");
        var noPayload = await SeedRawAsync(InventoryEvents.Imported, correlationId, T0.AddSeconds(1), null);
        var text = await SeedRawAsync(InventoryEvents.Imported, correlationId, T0.AddSeconds(2), "\"imported\"");
        var list = await SeedRawAsync(InventoryEvents.Imported, correlationId, T0.AddSeconds(3), """[{"referenceId":"A"}]""");
        var empty = await SeedRawAsync(InventoryEvents.Imported, correlationId, T0.AddSeconds(4), "{}");

        var items = (await ListAsync(_manager, $"correlationId={correlationId}&sortDir=asc")).Items;

        Assert.Equal([unknown.LogId, noPayload.LogId, text.LogId, list.LogId, empty.LogId], items.Select(i => i.LogId));
        Assert.All(items.Take(4), i => Assert.Null(i.Details));
        // A known type whose payload has none of the fields: an object without fields, not null.
        Assert.NotNull(items[4].Details);
        Assert.Empty(items[4].Details!);
        Assert.Equal("order.shipped", items[0].EventType);
    }

    [Fact]
    public async Task Actor_and_target_are_resolved_to_e_mail_addresses_and_an_actor_that_is_no_user_is_nobody()
    {
        var correlationId = NewCorrelationId();
        var (admin, manager, staff) = (factory.UserIdOf(AppRoles.Admin), factory.UserIdOf(AppRoles.Manager), factory.UserIdOf(AppRoles.Staff));
        var locked = await SeedAsync(UserEvents.Locked, correlationId, T0, new { targetUserId = staff, actorUserId = admin });
        var retried = await SeedAsync(ProcessingEvents.RetryRequested, correlationId, T0.AddSeconds(1), new { taskId = Guid.NewGuid(), requestedBy = manager });
        var acknowledged = await SeedAsync(AlertEvents.Acknowledged, correlationId, T0.AddSeconds(2), new { alertId = Guid.NewGuid(), type = "LowStock", acknowledgedBy = manager });
        var bySystem = await SeedAsync(AlertEvents.Acknowledged, correlationId, T0.AddSeconds(3), new { alertId = Guid.NewGuid(), type = "LowStock", acknowledgedBy = "system" });
        var byWorker = await SeedAsync(UserEvents.Unlocked, correlationId, T0.AddSeconds(4), new { targetUserId = staff, actorUserId = "system:order-worker" });
        var gone = await SeedAsync(UserEvents.Created, correlationId, T0.AddSeconds(5), new { targetUserId = Guid.NewGuid(), actorUserId = Guid.NewGuid(), role = "Staff" });
        var noActor = await SeedAsync(InventoryEvents.Imported, correlationId, T0.AddSeconds(6), new { referenceId = "A", itemCount = 1, totalQuantity = 1 });
        // The id is in the payload, but the event type has no entry: nothing of the payload is read.
        var unknown = await SeedAsync("order.shipped", correlationId, T0.AddSeconds(7), new { actorUserId = admin, targetUserId = staff });

        var items = (await ListAsync(_admin, $"correlationId={correlationId}&sortDir=asc")).Items;

        Assert.Equal(
            [
                (locked.LogId, "admin@integration.test", "staff@integration.test"),
                (retried.LogId, "manager@integration.test", null),
                (acknowledged.LogId, "manager@integration.test", null),
                (bySystem.LogId, null, null),
                (byWorker.LogId, null, "staff@integration.test"),
                (gone.LogId, null, null),
                (noActor.LogId, null, null),
                (unknown.LogId, null, null)
            ],
            items.Select(i => (i.LogId, i.ActorEmail, i.TargetEmail)));
    }

    [Fact]
    public async Task Import_and_acknowledge_through_the_api_are_listed_with_their_details_and_the_manager_who_did_it()
    {
        var correlationId = NewCorrelationId();
        var manager = ClientWithCorrelation(AppRoles.Manager, correlationId);
        var created = await manager.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest($"SYSEVT-{TestData.UniqueSuffix()}".ToUpperInvariant(), $"Event product {TestData.UniqueSuffix()}", 10m, 5, null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var product = await created.ReadAsync<ProductResponse>();

        // Three units: still low, so the alert of the new product stays open and can be acknowledged.
        var import = await manager.PostAsJsonAsync(
            "/api/inventory/import",
            new ImportInventoryRequest($"GRN-{TestData.UniqueSuffix()}", null, [new ImportInventoryItem(product.ProductId, 3)]));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var referenceId = (await import.ReadAsync<ImportInventoryResponse>()).ReferenceId;
        var alerts = await (await manager.GetAsync($"/api/alerts?productId={product.ProductId}")).ReadAsync<PagedResult<AlertResponse>>();
        var alert = Assert.Single(alerts.Items);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync($"/api/alerts/{alert.AlertId}/acknowledge", null)).StatusCode);

        var items = (await ListAsync(_manager, $"correlationId={correlationId}&sortDir=asc")).Items;

        Assert.Equal([InventoryEvents.Imported, AlertEvents.Acknowledged], items.Select(i => i.EventType));
        Assert.All(items, i => Assert.Equal((SystemEventLevel.Information, (string?)correlationId), (i.Level, i.CorrelationId)));

        var (imported, acknowledged) = (items[0], items[1]);
        Assert.True(
            JsonNode.DeepEquals(new JsonObject { ["referenceId"] = referenceId, ["itemCount"] = 1, ["totalQuantity"] = 3 }, imported.Details),
            imported.Details?.ToJsonString());
        // The import names nobody in its payload (the movement does).
        Assert.Equal(((string?)null, (string?)null), (imported.ActorEmail, imported.TargetEmail));

        Assert.True(
            JsonNode.DeepEquals(
                new JsonObject
                {
                    ["alertId"] = alert.AlertId.ToString(),
                    ["type"] = "LowStock",
                    ["acknowledgedBy"] = factory.UserIdOf(AppRoles.Manager).ToString()
                },
                acknowledged.Details),
            acknowledged.Details?.ToJsonString());
        Assert.Equal(("manager@integration.test", (string?)null), (acknowledged.ActorEmail, acknowledged.TargetEmail));
    }

    [Fact]
    public async Task User_management_through_the_api_is_listed_for_the_admin_with_actor_and_target_and_not_for_the_manager()
    {
        var correlationId = NewCorrelationId();
        var admin = ClientWithCorrelation(AppRoles.Admin, correlationId);
        var email = $"sysevt.{TestData.UniqueSuffix()}@users.test";
        var createdResponse = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, $"Passw0rd-{Guid.NewGuid():N}", AppRoles.Staff));
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var user = await createdResponse.ReadAsync<UserResponse>();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/users/{user.UserId}/role", new ChangeUserRoleRequest(AppRoles.Manager))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/users/{user.UserId}/lock", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/users/{user.UserId}/unlock", null)).StatusCode);
        var reset = await admin.PostAsJsonAsync($"/api/users/{user.UserId}/reset-password", new ResetUserPasswordRequest($"Passw0rd-{Guid.NewGuid():N}"));
        Assert.True(reset.IsSuccessStatusCode, reset.StatusCode.ToString());

        var response = await _admin.GetAsync($"/api/system-events?correlationId={correlationId}&sortDir=asc");
        var body = await response.Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<PagedResult<SystemEventResponse>>(body, TestData.Json)!.Items;

        Assert.Equal(
            [UserEvents.Created, UserEvents.RoleChanged, UserEvents.Locked, UserEvents.Unlocked, UserEvents.PasswordReset],
            items.Select(i => i.EventType));
        Assert.All(items, i => Assert.Equal(("admin@integration.test", (string?)email), (i.ActorEmail, i.TargetEmail)));

        JsonObject DetailsWith(params (string Name, string Value)[] more)
        {
            var details = new JsonObject
            {
                ["targetUserId"] = user.UserId.ToString(),
                ["actorUserId"] = factory.UserIdOf(AppRoles.Admin).ToString()
            };
            foreach (var (name, value) in more)
            {
                details[name] = value;
            }

            return details;
        }

        JsonObject[] expected =
        [
            DetailsWith(("role", AppRoles.Staff)), DetailsWith(("from", AppRoles.Staff), ("to", AppRoles.Manager)), DetailsWith(), DetailsWith(),
            DetailsWith()
        ];
        Assert.All(items.Zip(expected), pair => Assert.True(JsonNode.DeepEquals(pair.Second, pair.First.Details), pair.First.Details?.ToJsonString()));
        Assert.DoesNotContain("Passw0rd", body);

        // The manager: none of them, by correlation id and by name.
        Assert.Empty((await ListAsync(_manager, $"correlationId={correlationId}")).Items);
        Assert.Empty((await ListAsync(_manager, $"correlationId={correlationId}&eventType={UserEvents.Locked}")).Items);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static string NewCorrelationId() => $"sysevt-{Guid.NewGuid():N}";

    private static string Iso(DateTimeOffset instant) => Uri.EscapeDataString(instant.ToString("O"));

    private static List<long> Ids(PagedResult<SystemEventResponse> page) => page.Items.Select(i => i.LogId).ToList();

    private static async Task<PagedResult<SystemEventResponse>> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/system-events?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<PagedResult<SystemEventResponse>>();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToArray();
    }

    /// <summary>A client of the shared user in <paramref name="role"/> whose requests all carry <paramref name="correlationId"/>.</summary>
    private HttpClient ClientWithCorrelation(string role, string correlationId)
    {
        var client = factory.CreateClientAs(role);
        client.DefaultRequestHeaders.Add(CorrelationHeader, correlationId);
        return client;
    }

    /// <summary>Stores an event as a service does: the payload serialised with the message format.</summary>
    private Task<SystemEventLog> SeedAsync(
        string eventType,
        string correlationId,
        DateTimeOffset createdAt,
        object? payload = null,
        SystemEventLevel level = SystemEventLevel.Information,
        Guid? taskId = null,
        Guid? orderId = null,
        string? workerName = null) =>
        SeedRawAsync(eventType, correlationId, createdAt, payload is null ? null : MessageJson.Serialize(payload), level, taskId, orderId, workerName);

    private async Task<SystemEventLog> SeedRawAsync(
        string eventType,
        string correlationId,
        DateTimeOffset createdAt,
        string? payload,
        SystemEventLevel level = SystemEventLevel.Information,
        Guid? taskId = null,
        Guid? orderId = null,
        string? workerName = null)
    {
        var log = new SystemEventLog(level, eventType, correlationId, taskId, orderId, workerName, payload, createdAt);
        await factory.QueryDbAsync(async db =>
        {
            db.SystemEventLogs.Add(log);
            return await db.SaveChangesAsync();
        });
        return log;
    }
}
