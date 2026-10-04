using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Workers.ReportExportWorker;
using static Omnichannel.Tests.Integration.ReportExportTesting;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 020: the export endpoints without a worker - what a request stores, what it refuses, and who may read what.
/// Every test uses users of its own: exports and their limit are per user.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReportExportEndpointsTests(ApiFactory factory)
{
    private const string OrdersPath = "/api/reports/orders/export";
    private const string InventoryPath = "/api/reports/inventory/export";

    private static readonly DateTimeOffset From = new(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset To = new(2026, 9, 30, 23, 59, 59, TimeSpan.FromHours(7));

    // ---- a request ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ReportType.Orders)]
    [InlineData(ReportType.Inventory)]
    public async Task A_request_stores_task_export_message_and_audit_event_and_answers_202_with_the_task(ReportType reportType)
    {
        var (manager, managerId, managerEmail) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var correlationId = $"export-{TestData.UniqueSuffix()}";
        var before = DateTimeOffset.UtcNow;

        using var request = new HttpRequestMessage(HttpMethod.Post, reportType == ReportType.Orders ? OrdersPath : InventoryPath)
        {
            Content = JsonContent.Create<object>(
                reportType == ReportType.Orders
                    ? new ExportOrdersRequest(" POS ", OrderStatus.Reserved, null, "  ", From, To, "Asia/Ho_Chi_Minh")
                    : new ExportInventoryRequest(" bolt ", true, "Asia/Ho_Chi_Minh"),
                options: TestData.Json)
        };
        request.Headers.Add("X-Correlation-Id", correlationId);
        request.Headers.AcceptLanguage.ParseAdd("vi");
        var response = await manager.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var taskId = (await response.ReadAsync<ExportAcceptedResponse>()).TaskId;
        Assert.Equal($"/api/reports/exports/{taskId}", response.Headers.Location?.AbsolutePath);

        // The task: waiting, and it knows its message.
        var task = await TaskAsync(factory, taskId);
        Assert.Equal(
            (ProcessingTaskType.ReportExport, ProcessingTaskStatus.Pending, correlationId, (Guid?)null),
            (task.TaskType, task.Status, task.CorrelationId, task.OrderId));
        Assert.Empty(task.Attempts);

        // The export: what was asked for, by whom, until when - and no result yet.
        var export = await ExportAsync(factory, taskId);
        Assert.Equal((reportType, managerId.ToString()), (export.ReportType, export.CreatedBy));
        Assert.Matches($@"^{reportType.ToString().ToLowerInvariant()}_\d{{8}}-\d{{4}}\.xlsx$", export.FileName);
        Assert.InRange(export.ExpiresAt, before.AddDays(7).AddSeconds(-1), DateTimeOffset.UtcNow.AddDays(7).AddSeconds(1));
        Assert.Equal(
            ((string?)null, (long?)null, (int?)null, (DateTimeOffset?)null, (DateTimeOffset?)null),
            (export.ObjectKey, export.SizeBytes, export.RowCount, export.CompletedAt, export.DeletedAt));
        var parameters = JsonNode.Parse(export.Parameters)!.AsObject();
        Assert.Equal(("Asia/Ho_Chi_Minh", "vi"), (parameters["timeZone"]!.GetValue<string>(), parameters["language"]!.GetValue<string>()));
        if (reportType == ReportType.Orders)
        {
            // Normalised: the slug trimmed and lower-case, a blank search as none, the range in UTC.
            Assert.Equal(("pos", "Reserved"), (parameters["channel"]!.GetValue<string>(), parameters["status"]!.GetValue<string>()));
            Assert.Null(parameters["search"]);
            Assert.Null(parameters["customerId"]);
            Assert.Equal((From, To), (parameters["from"]!.GetValue<DateTimeOffset>(), parameters["to"]!.GetValue<DateTimeOffset>()));
            Assert.Equal(TimeSpan.Zero, parameters["from"]!.GetValue<DateTimeOffset>().Offset);
        }
        else
        {
            Assert.Equal(("bolt", true), (parameters["search"]!.GetValue<string>(), parameters["lowStockOnly"]!.GetValue<bool>()));
        }

        // The message: it names the task and nothing else.
        var message = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.AggregateId == taskId.ToString()));
        Assert.Equal(
            (task.MessageId, EventTypes.ReportExportRequested, OutboxEventStatus.Pending, correlationId, (string?)null),
            (message.OutboxEventId.ToString(), message.EventType, message.Status, message.CorrelationId, message.RoutingKey));
        Assert.Equal(["taskId"], JsonNode.Parse(message.Payload)!.AsObject().Select(p => p.Key));
        Assert.Equal(taskId, MessageEnvelope.FromOutboxEvent(message).TaskId);

        // The audit event, and the same seen through the Logs endpoint: who asked for which report.
        var audit = await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking().SingleAsync(l => l.TaskId == taskId));
        Assert.Equal(
            (ReportEvents.ExportRequested, SystemEventLevel.Information, correlationId, (string?)null),
            (audit.EventType, audit.Level, audit.CorrelationId, audit.WorkerName));
        var listed = await factory.CreateClientAs(AppRoles.Admin).GetStringAsync($"/api/system-events?taskId={taskId}");
        using var page = JsonDocument.Parse(listed);
        var item = page.RootElement.GetProperty("items")[0];
        Assert.Equal(
            (ReportEvents.ExportRequested, managerEmail, reportType.ToString(), managerId.ToString()),
            (item.GetProperty("eventType").GetString(), item.GetProperty("actorEmail").GetString(),
                item.GetProperty("details").GetProperty("reportType").GetString(), item.GetProperty("details").GetProperty("requestedBy").GetString()));
        Assert.Equal(["reportType", "requestedBy"], item.GetProperty("details").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_failure_before_the_commit_stores_none_of_the_four()
    {
        var (manager, managerId, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var correlationId = $"export-{TestData.UniqueSuffix()}";

        using var request = new HttpRequestMessage(HttpMethod.Post, InventoryPath)
        {
            Content = JsonContent.Create(new ExportInventoryRequest(null, false, "UTC"), options: TestData.Json)
        };
        request.Headers.Add("X-Correlation-Id", correlationId);
        factory.CommitFailures.FailNextCommit();
        var response = await manager.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var stored = await factory.QueryDbAsync(async db => (
            Tasks: await db.ProcessingTasks.CountAsync(t => t.CorrelationId == correlationId),
            Exports: await db.ReportExports.CountAsync(e => e.CreatedBy == managerId.ToString()),
            Messages: await db.OutboxEvents.CountAsync(e => e.CorrelationId == correlationId),
            Events: await db.SystemEventLogs.CountAsync(l => l.CorrelationId == correlationId)));
        Assert.Equal((0, 0, 0, 0), stored);

        // The same request again, without the failure: all four.
        await RequestAsync(manager, InventoryPath, new ExportInventoryRequest(null, false, "UTC"));
        Assert.Equal(1, await factory.QueryDbAsync(db => db.ReportExports.CountAsync(e => e.CreatedBy == managerId.ToString())));
    }

    // ---- what a request refuses --------------------------------------------------------------------------------

    public static TheoryData<string, string, string, string, string> InvalidRequests()
    {
        const string from = "2026-09-01T00:00:00+07:00";
        const string to = "2026-09-30T00:00:00+07:00";
        string Orders(string body) => body;

        return new TheoryData<string, string, string, string, string>
        {
            // Messages this application writes itself: exact in both languages.
            {
                OrdersPath, Orders($$"""{"from":"{{to}}","to":"{{from}}","timeZone":"UTC"}"""), "to",
                "'To date' must not be earlier than 'From date'.", "'Đến ngày' không được sớm hơn 'Từ ngày'."
            },
            {
                OrdersPath, Orders("""{"from":"2025-09-01T00:00:00Z","to":"2026-09-02T00:00:01Z","timeZone":"UTC"}"""), "to",
                "The range from 'From date' to 'To date' must not be longer than 366 days.",
                "Khoảng thời gian từ 'Từ ngày' đến 'Đến ngày' không được dài quá 366 ngày."
            },
            {
                OrdersPath, Orders($$"""{"from":"{{from}}","to":"{{to}}","timeZone":"Mars/Olympus_Mons"}"""), "timeZone",
                "'Time zone' must be the name of a time zone, for example Asia/Ho_Chi_Minh.",
                "'Múi giờ' phải là tên một múi giờ, ví dụ Asia/Ho_Chi_Minh."
            },
            {
                InventoryPath, """{"timeZone":"+07:00"}""", "timeZone",
                "'Time zone' must be the name of a time zone, for example Asia/Ho_Chi_Minh.",
                "'Múi giờ' phải là tên một múi giờ, ví dụ Asia/Ho_Chi_Minh."
            },
            // FluentValidation's own messages: the English text, and the field's Vietnamese name in the Vietnamese one.
            { OrdersPath, Orders($$"""{"to":"{{to}}","timeZone":"UTC"}"""), "from", "'From date' must not be empty.", "'Từ ngày' " },
            { OrdersPath, Orders($$"""{"from":"{{from}}","timeZone":"UTC"}"""), "to", "'To date' must not be empty.", "'Đến ngày' " },
            { OrdersPath, Orders($$"""{"from":"{{from}}","to":"{{to}}"}"""), "timeZone", "'Time zone' must not be empty.", "'Múi giờ' " },
            { OrdersPath, Orders($$"""{"from":"{{from}}","to":"{{to}}","timeZone":" "}"""), "timeZone", "'Time zone' must not be empty.", "'Múi giờ' " },
            {
                OrdersPath, Orders($$"""{"from":"{{from}}","to":"{{to}}","timeZone":"UTC","status":42}"""), "status",
                "'Status' has a range of values which does not include '42'.", "'Trạng thái' "
            },
            { InventoryPath, """{"search":"x"}""", "timeZone", "'Time zone' must not be empty.", "'Múi giờ' " },
            {
                InventoryPath, $$"""{"timeZone":"UTC","search":"{{new string('s', 101)}}"}""", "search",
                "The length of 'Search text' must be 100 characters or fewer. You entered 101 characters.", "'Từ khoá tìm kiếm' "
            }
        };
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task An_invalid_request_is_a_400_on_its_field_in_the_language_of_the_request_and_stores_nothing(
        string path, string body, string field, string english, string vietnamese)
    {
        var (manager, managerId, _) = await factory.CreateUserClientAsync(AppRoles.Manager);

        var (englishStatus, englishErrors) = await PostAsync(manager, path, body, "en");
        var (vietnameseStatus, vietnameseErrors) = await PostAsync(manager, path, body, "vi");

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (englishStatus, vietnameseStatus));
        Assert.Equal(new Dictionary<string, string[]> { [field] = [english] }, englishErrors);
        var translated = Assert.Single(Assert.Single(vietnameseErrors, e => e.Key == field).Value);
        Assert.Single(vietnameseErrors);
        Assert.NotEqual(english, translated);
        // Either the whole text (a message of this application) or the field's Vietnamese name in it (a built-in message).
        if (vietnamese.EndsWith(' '))
        {
            Assert.Contains(vietnamese.TrimEnd(), translated, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(vietnamese, translated);
        }

        Assert.Equal(0, await factory.QueryDbAsync(db => db.ReportExports.CountAsync(e => e.CreatedBy == managerId.ToString())));
    }

    [Fact]
    public async Task A_status_that_is_no_status_and_a_body_that_is_no_request_are_400s_and_the_longest_range_is_accepted()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);

        var unknownStatus = await PostAsync(manager, OrdersPath, """{"from":"2026-09-01T00:00:00Z","to":"2026-09-02T00:00:00Z","timeZone":"UTC","status":"Shipped"}""", "en");
        var noBody = await manager.PostAsync(InventoryPath, new StringContent("", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, unknownStatus.Status);
        Assert.Contains(unknownStatus.Errors.Keys, key => key.EndsWith("status", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);

        // Exactly Reports:MaxRangeDays, a range of one instant, and every filter left out.
        await RequestAsync(manager, OrdersPath, new ExportOrdersRequest(null, null, null, null, To.AddDays(-366), To, "Europe/Berlin"));
        await RequestAsync(manager, OrdersPath, new ExportOrdersRequest(null, null, null, null, To, To, "UTC"));
    }

    [Fact]
    public async Task More_rows_than_an_export_may_hold_is_a_400_export_too_large_and_stores_nothing()
    {
        var (manager, managerId, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var token = TestData.UniqueSuffix().ToUpperInvariant();
        var products = await SeedProductsAsync(factory, ($"XT-{token}-A", "Large A", 1m, 0, 1, null), ($"XT-{token}-B", "Large B", 1m, 0, 1, null));
        var customer = await SeedCustomerAsync(factory, $"Large {token}");
        // One order with one line: two rows of an orders export.
        var order = await SeedOrderAsync(factory, "website", customer.CustomerId, $"XT-{token}", OrderStatus.Reserved, From.AddDays(1), (products[0].ProductId, 1, 1m));
        var inventory = new ExportInventoryRequest($"XT-{token}", false, "UTC");
        var orders = new ExportOrdersRequest(null, null, customer.CustomerId, null, From, To, "UTC");

        // The limit is configuration; the options object of the host is changed for this test (the collection runs one test at a time).
        var options = factory.Services.GetRequiredService<IOptions<ReportsOptions>>().Value;
        var maxRows = options.MaxRows;
        try
        {
            options.MaxRows = 1;

            foreach (var (path, request) in new (string, object)[] { (InventoryPath, inventory), (OrdersPath, orders) })
            {
                foreach (var (language, detail) in new[]
                {
                    ("en", "These filters match 2 rows; one export holds at most 1. Narrow the filters."),
                    ("vi", "Bộ lọc này khớp 2 dòng; một lần xuất chỉ chứa tối đa 1 dòng. Hãy thu hẹp bộ lọc.")
                })
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(request, options: TestData.Json) };
                    message.Headers.AcceptLanguage.ParseAdd(language);
                    var response = await manager.SendAsync(message);
                    using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

                    Assert.Equal(
                        (HttpStatusCode.BadRequest, ReportErrorCodes.ExportTooLarge, detail),
                        (response.StatusCode, problem.RootElement.GetProperty("errorCode").GetString(), problem.RootElement.GetProperty("detail").GetString()));
                }
            }

            Assert.Equal(0, await factory.QueryDbAsync(db => db.ReportExports.CountAsync(e => e.CreatedBy == managerId.ToString())));

            // Exactly the limit is an export.
            options.MaxRows = 2;
            await RequestAsync(manager, InventoryPath, inventory);
            await RequestAsync(manager, OrdersPath, orders);
        }
        finally
        {
            options.MaxRows = maxRows;
        }

        Assert.Equal(order.OrderId, (await factory.QueryDbAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.ExternalOrderId == $"XT-{token}"))).OrderId);
    }

    [Fact]
    public async Task A_user_with_three_unfinished_exports_gets_409_too_many_exports_until_one_has_finished()
    {
        var (manager, managerId, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var (other, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var request = new ExportInventoryRequest($"none-{TestData.UniqueSuffix()}", false, "UTC");

        var waiting = await RequestAsync(manager, InventoryPath, request);
        var running = await RequestAsync(manager, InventoryPath, request);
        var retrying = await RequestAsync(manager, InventoryPath, request);
        // Every state before the end counts: waiting, running, waiting for a retry.
        await factory.QueryDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var second = await db.ProcessingTasks.Include(t => t.Attempts).SingleAsync(t => t.TaskId == running);
            db.ProcessingAttempts.Add(second.StartAttempt(ReportExportMessageHandler.WorkerName, "i-1", now));
            var third = await db.ProcessingTasks.Include(t => t.Attempts).SingleAsync(t => t.TaskId == retrying);
            var attempt = third.StartAttempt(ReportExportMessageHandler.WorkerName, "i-1", now);
            db.ProcessingAttempts.Add(attempt);
            third.ScheduleRetry(attempt.AttemptId, "storage_unavailable", "down", now);
            return await db.SaveChangesAsync();
        });

        foreach (var (language, detail) in new[]
        {
            ("en", "You already have 3 exports waiting or running. Wait until one of them has finished."),
            ("vi", "Bạn đang có 3 lần xuất tệp đang chờ hoặc đang chạy. Hãy đợi một lần xuất hoàn tất.")
        })
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, InventoryPath) { Content = JsonContent.Create(request, options: TestData.Json) };
            message.Headers.AcceptLanguage.ParseAdd(language);
            var refused = await manager.SendAsync(message);
            using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());

            Assert.Equal(
                (HttpStatusCode.Conflict, ReportErrorCodes.TooManyExports, detail),
                (refused.StatusCode, problem.RootElement.GetProperty("errorCode").GetString(), problem.RootElement.GetProperty("detail").GetString()));
        }

        Assert.Equal(3, await factory.QueryDbAsync(db => db.ReportExports.CountAsync(e => e.CreatedBy == managerId.ToString())));
        // The limit is per user, and it comes before the count of rows.
        await RequestAsync(other, InventoryPath, request);

        // One of the three ends (failed counts as finished): room for one more.
        await FailAsync(waiting, "storage_unavailable", "down");
        await RequestAsync(manager, InventoryPath, request);
    }

    // ---- roles -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Staff_get_403_and_an_anonymous_caller_401_on_every_export_endpoint()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var taskId = await RequestAsync(manager, InventoryPath, new ExportInventoryRequest(null, false, "UTC"));
        var staff = factory.CreateClientAs(AppRoles.Staff);
        var anonymous = factory.CreateAnonymousClient();
        var orders = JsonContent.Create(new ExportOrdersRequest(null, null, null, null, From, To, "UTC"), options: TestData.Json);
        var inventory = JsonContent.Create(new ExportInventoryRequest(null, false, "UTC"), options: TestData.Json);

        foreach (var (client, expected) in new[] { (staff, HttpStatusCode.Forbidden), (anonymous, HttpStatusCode.Unauthorized) })
        {
            Assert.Equal(expected, (await client.PostAsync(OrdersPath, orders)).StatusCode);
            Assert.Equal(expected, (await client.PostAsync(InventoryPath, inventory)).StatusCode);
            Assert.Equal(expected, (await client.GetAsync("/api/reports/exports")).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/reports/exports/{taskId}")).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/reports/exports/{taskId}/download")).StatusCode);
        }

        // An administrator may export as well.
        var (admin, _, _) = await factory.CreateUserClientAsync(AppRoles.Admin);
        await RequestAsync(admin, InventoryPath, new ExportInventoryRequest(null, false, "UTC"));
    }

    // ---- reading -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_detail_and_download_show_the_callers_own_exports_and_nobody_elses()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var (otherManager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var (admin, _, _) = await factory.CreateUserClientAsync(AppRoles.Admin);
        var ordersExport = await RequestAsync(owner, OrdersPath, new ExportOrdersRequest(null, null, null, null, From, To, "UTC"));
        var inventoryExport = await RequestAsync(owner, InventoryPath, new ExportInventoryRequest(null, true, "UTC"));
        var foreign = await RequestAsync(otherManager, InventoryPath, new ExportInventoryRequest(null, false, "UTC"));

        // The owner's list: its two exports, newest first.
        var list = await ListAsync(owner, "");
        Assert.Equal((2, 1, 20), (list.TotalCount, list.Page, list.PageSize));
        Assert.Equal([inventoryExport, ordersExport], list.Items.Select(e => e.TaskId));
        var item = list.Items[1];
        Assert.Equal(
            (ReportType.Orders, ReportExportStatus.Pending, (int?)null, (long?)null, (DateTimeOffset?)null, (string?)null),
            (item.ReportType, item.Status, item.RowCount, item.SizeBytes, item.CompletedAt, item.ErrorCode));
        Assert.Equal(("UTC", "en"), (item.Parameters!["timeZone"]!.GetValue<string>(), item.Parameters["language"]!.GetValue<string>()));
        Assert.Equal(item, await DetailAsync(owner, ordersExport), new ExportComparer());

        // Filter, direction and paging.
        Assert.Equal([ordersExport], (await ListAsync(owner, "?reportType=Orders")).Items.Select(e => e.TaskId));
        Assert.Equal([ordersExport, inventoryExport], (await ListAsync(owner, "?sortBy=createdAt&sortDir=asc")).Items.Select(e => e.TaskId));
        var second = await ListAsync(owner, "?pageSize=1&page=2");
        Assert.Equal((2, ordersExport), (second.TotalCount, Assert.Single(second.Items).TaskId));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/reports/exports?sortBy=fileName")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/reports/exports?reportType=Payments")).StatusCode);

        // Another manager and an administrator: the owner's exports do not exist for them - 404, never 403.
        foreach (var stranger in new[] { otherManager, admin })
        {
            foreach (var path in new[] { $"/api/reports/exports/{ordersExport}", $"/api/reports/exports/{ordersExport}/download" })
            {
                var response = await stranger.GetAsync(path);
                Assert.Equal((HttpStatusCode.NotFound, ReportErrorCodes.ExportNotFound), (response.StatusCode, await response.ReadErrorCodeAsync()));
            }
        }

        Assert.Equal([foreign], (await ListAsync(otherManager, "")).Items.Select(e => e.TaskId));
        Assert.Empty((await ListAsync(admin, "")).Items);
        // The same answer as for an export that does not exist at all.
        var unknown = await owner.GetAsync($"/api/reports/exports/{Guid.NewGuid()}");
        Assert.Equal((HttpStatusCode.NotFound, ReportErrorCodes.ExportNotFound), (unknown.StatusCode, await unknown.ReadErrorCodeAsync()));
    }

    [Fact]
    public async Task Status_error_code_and_download_follow_the_task_and_the_retention()
    {
        var (owner, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var request = new ExportInventoryRequest(null, false, "UTC");
        var waiting = await RequestAsync(owner, InventoryPath, request);
        var failed = await RequestAsync(owner, InventoryPath, request);
        var succeeded = await RequestAsync(owner, InventoryPath, request);
        const string secret = "Host=db.internal;Password=***; the storage said no";
        await FailAsync(failed, "storage_unavailable", secret);
        await factory.QueryDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var task = await db.ProcessingTasks.Include(t => t.Attempts).SingleAsync(t => t.TaskId == succeeded);
            var attempt = task.StartAttempt(ReportExportMessageHandler.WorkerName, "i-1", now);
            db.ProcessingAttempts.Add(attempt);
            (await db.ReportExports.SingleAsync(e => e.TaskId == succeeded)).Complete(4096, 12, now);
            task.Succeed(null, attempt.AttemptId, now);
            return await db.SaveChangesAsync();
        });
        var since = DateTimeOffset.UtcNow;

        // Not finished: nothing to download.
        foreach (var (language, detail) in new[]
        {
            ("en", "This export has no file to download: it has not finished, or it failed."),
            ("vi", "Lần xuất này chưa có tệp để tải: nó chưa hoàn tất hoặc đã thất bại.")
        })
        {
            foreach (var taskId in new[] { waiting, failed })
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, $"/api/reports/exports/{taskId}/download");
                message.Headers.AcceptLanguage.ParseAdd(language);
                var response = await owner.SendAsync(message);
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(
                    (HttpStatusCode.Conflict, ReportErrorCodes.ExportNotReady, detail),
                    (response.StatusCode, problem.RootElement.GetProperty("errorCode").GetString(), problem.RootElement.GetProperty("detail").GetString()));
            }
        }

        // Failed: the code of the last attempt, never its message.
        var failedBody = await owner.GetStringAsync($"/api/reports/exports/{failed}");
        var failedDetail = JsonSerializer.Deserialize<ReportExportResponse>(failedBody, TestData.Json)!;
        Assert.Equal((ReportExportStatus.Failed, "storage_unavailable"), (failedDetail.Status, failedDetail.ErrorCode));
        Assert.DoesNotContain("the storage said no", failedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("the storage said no", await owner.GetStringAsync("/api/reports/exports"), StringComparison.Ordinal);

        // Succeeded: the result, and a link to the file - computed locally (no storage is running in this test).
        var done = await DetailAsync(owner, succeeded);
        Assert.Equal((ReportExportStatus.Succeeded, (int?)12, (long?)4096, (string?)null), (done.Status, done.RowCount, done.SizeBytes, done.ErrorCode));
        var link = await (await owner.GetAsync($"/api/reports/exports/{succeeded}/download")).ReadAsync<ReportDownloadResponse>();
        Assert.Equal(done.FileName, link.FileName);
        Assert.InRange(link.ExpiresAt, since.AddMinutes(5).AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(1));
        var url = new Uri(link.Url);
        Assert.Equal($"/{StorageEmulator.Bucket}/exports/{succeeded}.xlsx", url.AbsolutePath);
        Assert.Contains("X-Amz-Signature=", url.Query, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=300", url.Query, StringComparison.Ordinal);
        // The link is a credential: it is in the answer and in no log event of the API.
        Assert.All(factory.Logs.RenderedSince(since), line =>
        {
            Assert.DoesNotContain("X-Amz-", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(url.Host + ":" + url.Port, line, StringComparison.Ordinal);
        });

        // Expired (the sweep deleted the file): the status says so, whatever the task ended as; no link any more.
        foreach (var taskId in new[] { failed, succeeded })
        {
            await factory.QueryDbAsync(async db =>
            {
                (await db.ReportExports.SingleAsync(e => e.TaskId == taskId)).MarkDeleted(DateTimeOffset.UtcNow);
                return await db.SaveChangesAsync();
            });
            var expired = await DetailAsync(owner, taskId);
            Assert.Equal((ReportExportStatus.Expired, (string?)null), (expired.Status, expired.ErrorCode));
            var response = await owner.GetAsync($"/api/reports/exports/{taskId}/download");
            Assert.Equal((HttpStatusCode.Conflict, ReportErrorCodes.ExportExpired), (response.StatusCode, await response.ReadErrorCodeAsync()));
        }

        Assert.Equal(
            [ReportExportStatus.Expired, ReportExportStatus.Expired, ReportExportStatus.Pending],
            (await ListAsync(owner, "")).Items.Select(e => e.Status));
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static async Task<(HttpStatusCode Status, Dictionary<string, string[]> Errors)> PostAsync(HttpClient client, string path, string json, string language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.AcceptLanguage.ParseAdd(language);
        var response = await client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = document.RootElement.TryGetProperty("errors", out var found)
            ? found.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(m => m.GetString()!).ToArray())
            : [];
        return (response.StatusCode, errors);
    }

    private static async Task<PagedResult<ReportExportResponse>> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/reports/exports{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<PagedResult<ReportExportResponse>>();
    }

    /// <summary>Ends an export's task as the worker would after a final failure: one failed attempt, task Failed.</summary>
    private Task FailAsync(Guid taskId, string errorCode, string errorMessage) =>
        factory.QueryDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var task = await db.ProcessingTasks.Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId);
            var attempt = task.StartAttempt(ReportExportMessageHandler.WorkerName, "i-1", now);
            db.ProcessingAttempts.Add(attempt);
            task.Fail(attempt.AttemptId, errorCode, errorMessage, now);
            return await db.SaveChangesAsync();
        });

    /// <summary>The same export: every field, the parameters compared as JSON.</summary>
    private sealed class ExportComparer : IEqualityComparer<ReportExportResponse>
    {
        public bool Equals(ReportExportResponse? x, ReportExportResponse? y) =>
            x is not null && y is not null
            && x with { Parameters = null } == y with { Parameters = null }
            && JsonNode.DeepEquals(x.Parameters, y.Parameters);

        public int GetHashCode(ReportExportResponse export) => export.TaskId.GetHashCode();
    }
}
