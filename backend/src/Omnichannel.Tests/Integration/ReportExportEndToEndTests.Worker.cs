using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
using Omnichannel.Infrastructure.Persistence.Reports;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Tests.Integration.Workers;
using Omnichannel.Workers.ReportExportWorker;
using RabbitMQ.Client;
using static Omnichannel.Tests.Integration.ReportExportTesting;

namespace Omnichannel.Tests.Integration;

// The inventory export, deliveries, failures and the retention sweep.
public sealed partial class ReportExportEndToEndTests
{
    private readonly HashSet<string?> _deadLettered = [];

    [Fact]
    public async Task An_inventory_export_lists_the_products_of_the_filter_with_their_stock()
    {
        var (manager, _, managerEmail) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var token = TestData.UniqueSuffix().ToUpperInvariant();
        var products = await SeedProductsAsync(
            factory,
            ($"XI-{token}-B", "Sold out", 99_000m, 0, 0, null),
            ($"XI-{token}-A", "@SUM(A1)", 150_000m, 2, 10, new DateOnly(2027, 1, 31)));
        await factory.QueryDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""UPDATE "Inventories" SET "ReservedStock" = 4 WHERE "ProductId" = {products[1].ProductId}"""));

        var before = DateTimeOffset.UtcNow;
        var all = await RequestInventoryAsync(manager, $" xi-{token.ToLowerInvariant()} ", language: "vi");
        var lowOnly = await RequestAsync(
            manager, "/api/reports/inventory/export", new ExportInventoryRequest($"XI-{token}", true, "Asia/Ho_Chi_Minh"), "en");

        using var workers = await StartWorkersAsync(factory, broker, "export-e2e-inventory");
        try
        {
            await WaitForStatusAsync(factory, all, ProcessingTaskStatus.Succeeded);
            await WaitForStatusAsync(factory, lowOnly, ProcessingTaskStatus.Succeeded);
        }
        finally
        {
            await workers.StopAsync();
        }

        var detail = await DetailAsync(manager, all);
        Assert.Equal((ReportType.Inventory, ReportExportStatus.Succeeded, (int?)2), (detail.ReportType, detail.Status, detail.RowCount));
        Assert.Matches(@"^inventory_\d{8}-\d{4}\.xlsx$", detail.FileName);
        Assert.Equal(
            ($"xi-{token.ToLowerInvariant()}", false, "Asia/Ho_Chi_Minh", "vi"),
            (detail.Parameters!["search"]!.GetValue<string>(), detail.Parameters["lowStockOnly"]!.GetValue<bool>(),
                detail.Parameters["timeZone"]!.GetValue<string>(), detail.Parameters["language"]!.GetValue<string>()));

        using var workbook = Open(await DownloadAsync(manager, all));
        Assert.Equal(["Tồn kho", "Thông tin"], workbook.Worksheets.Select(w => w.Name));

        var sheet = workbook.Worksheet("Tồn kho");
        Assert.Equal(
            ["SKU", "Tên sản phẩm", "Giá", "Tổng tồn", "Đang giữ", "Khả dụng", "Ngưỡng cảnh báo tồn kho", "Sắp hết hàng", "Hạn dùng",
                "Cập nhật tồn kho lúc (Asia/Ho_Chi_Minh)"],
            RowTexts(sheet, 1, 10));
        Assert.True(sheet.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(1, sheet.SheetView.SplitRow);
        Assert.Equal("A1:J3", sheet.AutoFilter.Range.RangeAddress.ToString());

        // Products by SKU.
        Assert.Equal([$"XI-{token}-A", $"XI-{token}-B"], new[] { 2, 3 }.Select(row => sheet.Cell(row, 1).GetString()));
        var name = sheet.Cell(2, 2);
        Assert.Equal((XLDataType.Text, false, "@SUM(A1)"), (name.DataType, name.HasFormula, name.GetText()));
        Assert.Equal(
            (XLDataType.Number, 150_000d, "#,##0"),
            (sheet.Cell(2, 3).DataType, sheet.Cell(2, 3).GetDouble(), sheet.Cell(2, 3).Style.NumberFormat.Format));
        Assert.Equal([10d, 4d, 6d, 2d], new[] { 4, 5, 6, 7 }.Select(column => sheet.Cell(2, column).GetDouble()));
        Assert.All(new[] { 4, 5, 6, 7 }, column => Assert.Equal(XLDataType.Number, sheet.Cell(2, column).DataType));
        // The shared low-stock rule: 6 available above a threshold of 2 is not low, 0 at a threshold of 0 is.
        Assert.Equal(("Không", "Có"), (sheet.Cell(2, 8).GetString(), sheet.Cell(3, 8).GetString()));
        // A date is a date; a product without one has an empty cell.
        Assert.Equal(
            (XLDataType.DateTime, new DateTime(2027, 1, 31), "yyyy-mm-dd"),
            (sheet.Cell(2, 9).DataType, sheet.Cell(2, 9).GetDateTime(), sheet.Cell(2, 9).Style.NumberFormat.Format));
        Assert.True(sheet.Cell(3, 9).IsEmpty());
        // The time of the stock row, as a clock in the requested zone showed it (UTC+7).
        Assert.Equal(XLDataType.DateTime, sheet.Cell(2, 10).DataType);
        Assert.InRange(sheet.Cell(2, 10).GetDateTime(), before.UtcDateTime.AddHours(7).AddSeconds(-5), DateTime.UtcNow.AddHours(7).AddSeconds(5));

        var info = workbook.Worksheet("Thông tin");
        Assert.Equal(
            ["Báo cáo", "Từ khoá tìm kiếm", "Chỉ hàng sắp hết", "Múi giờ", "Thời điểm tạo tệp (Asia/Ho_Chi_Minh)", "Người yêu cầu", "Số sản phẩm"],
            Enumerable.Range(1, 7).Select(row => info.Cell(row, 1).GetString()));
        Assert.Equal(
            ["Tồn kho", $"xi-{token.ToLowerInvariant()}", "Không", "Asia/Ho_Chi_Minh", managerEmail],
            new[] { 1, 2, 3, 4, 6 }.Select(row => info.Cell(row, 2).GetString()));
        Assert.Equal(2d, info.Cell(7, 2).GetDouble());

        // The low-stock filter of the inventory list: only the product that is low, and the file is in English.
        Assert.Equal(1, (await DetailAsync(manager, lowOnly)).RowCount);
        using var low = Open(await DownloadAsync(manager, lowOnly));
        Assert.Equal(["Inventory", "Info"], low.Worksheets.Select(w => w.Name));
        Assert.Equal(($"XI-{token}-B", "Yes"), (low.Worksheet("Inventory").Cell(2, 1).GetString(), low.Worksheet("Inventory").Cell(2, 8).GetString()));
        Assert.Equal(2, low.Worksheet("Inventory").LastRowUsed()!.RowNumber());
        Assert.Equal("Yes", low.Worksheet("Info").Cell(3, 2).GetString());
    }

    // ---- deliveries --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_second_delivery_of_a_finished_export_is_acknowledged_without_another_attempt_or_upload()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var taskId = await RequestInventoryAsync(manager, $"none-{TestData.UniqueSuffix()}");
        var probe = new ExportProbe();
        var logs = new CapturingLoggerProvider();

        using var workers = await StartWorkersAsync(
            factory, broker, "export-e2e-duplicate", configureServices: services => services.ProbeExports(probe).CaptureLogs(logs));
        try
        {
            await WaitForStatusAsync(factory, taskId, ProcessingTaskStatus.Succeeded);

            // The very same message again (publisher crash between confirm and commit, or a late retry copy).
            var message = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.AggregateId == taskId.ToString()));
            await PublishAsync(MessageJson.SerializeToUtf8Bytes(MessageEnvelope.FromOutboxEvent(message)), message.OutboxEventId.ToString());

            await Eventually.WaitAsync(
                () => Task.FromResult(logs.Entries.Count(e =>
                    e.Category == typeof(ReportExportMessageHandler).FullName
                    && e.Message.Contains("Duplicate delivery", StringComparison.Ordinal)
                    && Equals(e.Scope.GetValueOrDefault("TaskId"), taskId))),
                count => count == 1,
                WaitTimeout,
                "duplicate delivery acknowledged");
        }
        finally
        {
            await workers.StopAsync();
        }

        // An export without rows is a file with its headers.
        Assert.Equal(0, (await DetailAsync(manager, taskId)).RowCount);
        Assert.Single((await TaskAsync(factory, taskId)).Attempts);
        Assert.Equal(1, probe.PutsOf(taskId));
    }

    [Fact]
    public async Task A_failure_after_the_upload_and_before_the_commit_is_retried_and_stores_the_same_object_again()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var taskId = await RequestInventoryAsync(manager, $"none-{TestData.UniqueSuffix()}");
        var probe = new ExportProbe { OnlyTaskId = taskId, CompletionFailures = 1 };

        using var workers = await StartWorkersAsync(
            factory, broker, "export-e2e-redelivery", configureServices: services => services.ProbeExports(probe));
        try
        {
            await WaitForStatusAsync(factory, taskId, ProcessingTaskStatus.Succeeded);
        }
        finally
        {
            await workers.StopAsync();
        }

        var attempts = (await TaskAsync(factory, taskId)).Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal(
            [(1, ProcessingAttemptStatus.Failed, "timeout"), (2, ProcessingAttemptStatus.Succeeded, null)],
            attempts.Select(a => (a.AttemptNo, a.Status, a.ErrorCode)));
        // Stored twice, under the one key the task has: one object.
        Assert.Equal(2, probe.PutsOf(taskId));
        Assert.Equal([ReportExport.ObjectKeyOf(taskId)], await _storage.KeysAsync($"exports/{taskId}"));
        var export = await ExportAsync(factory, taskId);
        Assert.Equal(ReportExport.ObjectKeyOf(taskId), export.ObjectKey);
        Assert.Equal(export.SizeBytes, (await DownloadAsync(manager, taskId)).LongLength);
    }

    // ---- failures ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task While_the_storage_is_down_the_export_is_retried_and_succeeds_once_it_is_back()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var taskId = await RequestInventoryAsync(manager, $"none-{TestData.UniqueSuffix()}");

        await _storage.StopAsync();
        try
        {
            // Enough attempts for the outage (the last delay tier repeats).
            using var workers = await StartWorkersAsync(
                factory, broker, "export-e2e-outage", new Dictionary<string, string?> { ["ReportExportWorker:MaxAttempts"] = "20" });
            try
            {
                var waiting = await WaitForTaskAsync(
                    factory, taskId, t => t.Status == ProcessingTaskStatus.RetryScheduled && t.Attempts.Count >= 2, "retried while the storage is down");
                Assert.All(
                    waiting.Attempts.Where(a => a.Status == ProcessingAttemptStatus.Failed),
                    a => Assert.Equal(ObjectStorageException.ErrorCode, a.ErrorCode));
                Assert.Equal(ReportExportStatus.RetryScheduled, (await DetailAsync(manager, taskId)).Status);

                await _storage.StartAsync();
                await WaitForStatusAsync(factory, taskId, ProcessingTaskStatus.Succeeded);
            }
            finally
            {
                await workers.StopAsync();
            }
        }
        finally
        {
            await _storage.StartAsync();
        }

        var attempts = (await TaskAsync(factory, taskId)).Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal(ProcessingAttemptStatus.Succeeded, attempts[^1].Status);
        Assert.All(attempts[..^1], a => Assert.Equal((ProcessingAttemptStatus.Failed, ObjectStorageException.ErrorCode), (a.Status, a.ErrorCode)));
        Assert.Equal("PK"u8.ToArray(), (await DownloadAsync(manager, taskId))[..2]);
    }

    [Fact]
    public async Task An_export_that_fails_for_good_is_failed_alerted_and_dead_lettered_and_a_manual_retry_completes_it()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var taskId = await RequestInventoryAsync(manager, $"none-{TestData.UniqueSuffix()}");
        var logs = new CapturingLoggerProvider();

        await _storage.StopAsync();
        try
        {
            using var workers = await StartWorkersAsync(
                factory,
                broker,
                "export-e2e-failed",
                new Dictionary<string, string?> { ["ReportExportWorker:MaxAttempts"] = "2" },
                services => services.CaptureLogs(logs));
            try
            {
                var failed = await WaitForStatusAsync(factory, taskId, ProcessingTaskStatus.Failed);
                Assert.Equal(
                    [(1, ProcessingAttemptStatus.Failed, ObjectStorageException.ErrorCode), (2, ProcessingAttemptStatus.Failed, ObjectStorageException.ErrorCode)],
                    failed.Attempts.OrderBy(a => a.AttemptNo).Select(a => (a.AttemptNo, a.Status, a.ErrorCode)));
                await WaitForDeadLetterAsync(failed.MessageId!);

                // processing.failed and the alert of the task, as for every other worker.
                var failure = await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
                    .SingleAsync(l => l.EventType == ProcessingEvents.Failed && l.TaskId == taskId));
                var payload = JsonNode.Parse(failure.Payload!)!;
                Assert.Equal(
                    (nameof(ProcessingTaskType.ReportExport), ObjectStorageException.ErrorCode, RabbitMqTopology.ReportExportQueue, 2),
                    (payload["taskType"]!.GetValue<string>(), payload["errorCode"]!.GetValue<string>(), payload["queue"]!.GetValue<string>(),
                        payload["attemptNo"]!.GetValue<int>()));
                var alert = await AlertOfAsync(taskId);
                Assert.Equal((AlertType.ProcessingFailed, AlertStatus.Open, ObjectStorageException.ErrorCode), (alert.Type, alert.Status, alert.ErrorCode));

                // The requester sees the code of the failure and nothing else of it; there is nothing to download.
                var response = await manager.GetAsync($"/api/reports/exports/{taskId}");
                var body = await response.Content.ReadAsStringAsync();
                var detail = JsonSerializer.Deserialize<ReportExportResponse>(body, TestData.Json)!;
                Assert.Equal((ReportExportStatus.Failed, ObjectStorageException.ErrorCode, (int?)null), (detail.Status, detail.ErrorCode, detail.RowCount));
                Assert.DoesNotContain("errorMessage", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(failed.Attempts.First().ErrorMessage!, body, StringComparison.Ordinal);
                var download = await manager.GetAsync($"/api/reports/exports/{taskId}/download");
                Assert.Equal((HttpStatusCode.Conflict, ReportErrorCodes.ExportNotReady), (download.StatusCode, await download.ReadErrorCodeAsync()));

                // Nothing of the storage in what the worker stored or logged.
                var written = failed.Attempts.Select(a => a.ErrorMessage!).Append(failure.Payload!).Concat(logs.Entries.Select(e => e.Message)).ToList();
                Assert.All(written, text =>
                {
                    Assert.DoesNotContain(_storage.ServiceUrl, text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(_storage.AccessKeyId, text, StringComparison.Ordinal);
                    Assert.DoesNotContain(_storage.SecretAccessKey, text, StringComparison.Ordinal);
                    Assert.DoesNotContain("X-Amz-", text, StringComparison.OrdinalIgnoreCase);
                });
                Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Object storage put failed: ", StringComparison.Ordinal));

                // The storage is back: the manual retry of the processing API works unchanged.
                await _storage.StartAsync();
                var retry = await manager.PostAsync($"/api/processing/tasks/{taskId}/retry", null);
                Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);

                var done = await WaitForStatusAsync(factory, taskId, ProcessingTaskStatus.Succeeded);
                Assert.Equal((3, 2), (done.Attempts.Count, done.AttemptBudgetStart));
                Assert.Equal(AlertStatus.Resolved, (await AlertOfAsync(taskId)).Status);
            }
            finally
            {
                await workers.StopAsync();
            }
        }
        finally
        {
            await _storage.StartAsync();
        }

        var succeeded = await DetailAsync(manager, taskId);
        Assert.Equal((ReportExportStatus.Succeeded, (string?)null, (int?)0), (succeeded.Status, succeeded.ErrorCode, succeeded.RowCount));
        Assert.Equal("PK"u8.ToArray(), (await DownloadAsync(manager, taskId))[..2]);
    }

    [Fact]
    public async Task More_rows_than_the_limit_at_the_worker_fail_the_export_at_once()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var token = TestData.UniqueSuffix().ToUpperInvariant();
        await SeedProductsAsync(factory, ($"XG-{token}-A", "Grew A", 1m, 0, 1, null), ($"XG-{token}-B", "Grew B", 1m, 0, 1, null));
        // Accepted by the API (its limit is 100,000): the data "grew" past the worker's limit afterwards.
        var taskId = await RequestInventoryAsync(manager, $"XG-{token}");
        var probe = new ExportProbe();

        using var workers = await StartWorkersAsync(
            factory, broker, "export-e2e-too-large", new Dictionary<string, string?> { ["Reports:MaxRows"] = "1" }, services => services.ProbeExports(probe));
        try
        {
            var failed = await WaitForStatusAsync(factory, taskId, ProcessingTaskStatus.Failed);
            // No retry: one attempt, and the message is in the DLQ.
            var attempt = Assert.Single(failed.Attempts);
            Assert.Equal((ProcessingAttemptStatus.Failed, ReportErrorCodes.ExportTooLarge), (attempt.Status, attempt.ErrorCode));
            await WaitForDeadLetterAsync(failed.MessageId!);
        }
        finally
        {
            await workers.StopAsync();
        }

        Assert.Equal(0, probe.PutsOf(taskId));
        Assert.False(await _storage.ExistsAsync(ReportExport.ObjectKeyOf(taskId)));
        var detail = await DetailAsync(manager, taskId);
        Assert.Equal((ReportExportStatus.Failed, ReportErrorCodes.ExportTooLarge), (detail.Status, detail.ErrorCode));
        Assert.Equal(AlertStatus.Open, (await AlertOfAsync(taskId)).Status);
    }

    [Fact]
    public async Task An_export_that_expired_before_it_was_built_fails_without_a_file()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var taskId = await RequestInventoryAsync(manager, $"none-{TestData.UniqueSuffix()}");
        // The Workers host was down for longer than the retention time.
        await ExpireAsync(factory, taskId, TimeSpan.FromMinutes(1));
        var probe = new ExportProbe();

        using var workers = await StartWorkersAsync(factory, broker, "export-e2e-expired", configureServices: services => services.ProbeExports(probe));
        try
        {
            var failed = await WaitForStatusAsync(factory, taskId, ProcessingTaskStatus.Failed);
            var attempt = Assert.Single(failed.Attempts);
            Assert.Equal((ProcessingAttemptStatus.Failed, ReportErrorCodes.ExportExpired), (attempt.Status, attempt.ErrorCode));
        }
        finally
        {
            await workers.StopAsync();
        }

        Assert.Equal(0, probe.PutsOf(taskId));
        // For its requester the export is simply expired: it is finished and past its retention time (the code of
        // the failure stays with the task).
        var detail = await DetailAsync(manager, taskId);
        Assert.Equal((ReportExportStatus.Expired, (string?)null), (detail.Status, detail.ErrorCode));
    }

    [Fact]
    public async Task Messages_that_can_never_be_processed_are_logged_with_their_code_and_dead_lettered()
    {
        var unknownTask = Guid.CreateVersion7();
        // A task that exists but is no export.
        var orderTask = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, $"poison-{TestData.UniqueSuffix()}", null);
        await factory.QueryDbAsync(async db =>
        {
            db.ProcessingTasks.Add(orderTask);
            return await db.SaveChangesAsync();
        });
        var suffix = TestData.UniqueSuffix();
        var messages = new (string MessageId, byte[] Body, string Code, Guid? TaskId)[]
        {
            ($"bad-json-{suffix}", Encoding.UTF8.GetBytes("{ not json"), MessageRejectionCodes.InvalidJson, null),
            ($"mismatch-{suffix}", Envelope($"mismatch-{suffix}", orderTask.TaskId), MessageRejectionCodes.TaskMismatch, orderTask.TaskId),
            ($"unknown-{suffix}", Envelope($"unknown-{suffix}", unknownTask), MessageRejectionCodes.UnknownTask, unknownTask)
        };

        using var workers = await StartWorkersAsync(factory, broker, "export-e2e-poison");
        try
        {
            foreach (var message in messages)
            {
                await PublishAsync(message.Body, message.MessageId);
            }

            foreach (var message in messages)
            {
                await WaitForDeadLetterAsync(message.MessageId);
            }
        }
        finally
        {
            await workers.StopAsync();
        }

        var rejected = (await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
                .Where(l => l.EventType == ReportEvents.MessageRejected)
                .ToListAsync()))
            .Where(l => l.Payload!.Contains(suffix, StringComparison.Ordinal))
            .ToList();
        Assert.Equal(
            messages.Select(m => (m.MessageId, m.Code, m.TaskId)),
            rejected
                .Select(l => (JsonNode.Parse(l.Payload!)!["messageId"]!.GetValue<string>(), JsonNode.Parse(l.Payload!)!["reasonCode"]!.GetValue<string>(), l.TaskId))
                .OrderBy(m => m.Item1, StringComparer.Ordinal));
        Assert.All(rejected, l => Assert.Equal((SystemEventLevel.Error, ReportExportMessageHandler.WorkerName), (l.Level, l.WorkerName)));
        // The task that is no export was not touched.
        Assert.Equal(ProcessingTaskStatus.Pending, (await TaskAsync(factory, orderTask.TaskId)).Status);

        // The Logs endpoint shows the code and never the text of the rejection.
        var listed = await factory.CreateClientAs(AppRoles.Manager)
            .GetStringAsync($"/api/system-events?eventType={ReportEvents.MessageRejected}&taskId={unknownTask}");
        using var page = JsonDocument.Parse(listed);
        var details = page.RootElement.GetProperty("items")[0].GetProperty("details");
        Assert.Equal(["messageId", "reasonCode"], details.EnumerateObject().Select(p => p.Name));
        Assert.Equal(MessageRejectionCodes.UnknownTask, details.GetProperty("reasonCode").GetString());
    }

    // ---- retention ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sweep_deletes_the_files_of_expired_finished_exports_and_leaves_every_other_export_alone()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var token = TestData.UniqueSuffix().ToUpperInvariant();
        await SeedProductsAsync(factory, ($"XS-{token}-A", "Sweep A", 1m, 0, 1, null), ($"XS-{token}-B", "Sweep B", 1m, 0, 1, null));

        // Two succeeded exports (one expires, one does not) and one that failed after its file was stored.
        var expired = await RequestInventoryAsync(manager, $"none-{token}");
        var kept = await RequestInventoryAsync(manager, $"none-{token}");
        using (var workers = await StartWorkersAsync(factory, broker, "export-e2e-sweep"))
        {
            await WaitForStatusAsync(factory, expired, ProcessingTaskStatus.Succeeded);
            await WaitForStatusAsync(factory, kept, ProcessingTaskStatus.Succeeded);
            await workers.StopAsync();
        }

        var failed = await RequestInventoryAsync(manager, $"XS-{token}");
        using (var workers = await StartWorkersAsync(factory, broker, "export-e2e-sweep", new Dictionary<string, string?> { ["Reports:MaxRows"] = "1" }))
        {
            await WaitForStatusAsync(factory, failed, ProcessingTaskStatus.Failed);
            await workers.StopAsync();
        }

        await _storage.PutAsync(ReportExport.ObjectKeyOf(failed), [1, 2, 3], ReportFiles.ContentType);
        // And one that nobody has built yet (no Workers host is running now).
        var waiting = await RequestInventoryAsync(manager, $"none-{token}");

        foreach (var taskId in new[] { expired, failed, waiting })
        {
            await ExpireAsync(factory, taskId, TimeSpan.FromSeconds(30));
        }

        // One export per batch: the round goes on until none is left.
        using var host = await BuildWorkersAsync(
            factory, broker.HostName, broker.Port, "export-e2e-sweep-run", new Dictionary<string, string?> { ["Reports:CleanupBatchSize"] = "1" });
        var before = DateTimeOffset.UtcNow;
        var result = await SweepAsync(host);

        Assert.True(result.Expired >= 2, $"expired: {result.Expired}");
        Assert.Equal(0, result.Failed);
        foreach (var taskId in new[] { expired, failed })
        {
            Assert.False(await _storage.ExistsAsync(ReportExport.ObjectKeyOf(taskId)));
            var export = await ExportAsync(factory, taskId);
            Assert.InRange(export.DeletedAt!.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.Equal("system:workers", export.UpdatedBy);
            Assert.Equal(ReportExportStatus.Expired, (await DetailAsync(manager, taskId)).Status);
        }

        // Not expired: untouched. Expired but not finished: left for the worker, which fails it as expired.
        Assert.True(await _storage.ExistsAsync(ReportExport.ObjectKeyOf(kept)));
        Assert.Null((await ExportAsync(factory, kept)).DeletedAt);
        Assert.Null((await ExportAsync(factory, waiting)).DeletedAt);
        Assert.Equal(ReportExportStatus.Pending, (await DetailAsync(manager, waiting)).Status);

        // The requester: the result of the expired export stays readable, its file is gone.
        var detail = await DetailAsync(manager, expired);
        Assert.Equal(((int?)0, (string?)null), (detail.RowCount, detail.ErrorCode));
        Assert.NotNull(detail.CompletedAt);
        Assert.Null((await DetailAsync(manager, failed)).ErrorCode);
        foreach (var (language, message) in new[]
        {
            ("en", "This export has expired and its file was deleted. Request it again."),
            ("vi", "Lần xuất này đã hết hạn và tệp đã bị xoá. Hãy yêu cầu xuất lại.")
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/reports/exports/{expired}/download");
            request.Headers.AcceptLanguage.ParseAdd(language);
            var download = await manager.SendAsync(request);
            using var problem = JsonDocument.Parse(await download.Content.ReadAsStringAsync());
            Assert.Equal(
                (HttpStatusCode.Conflict, ReportErrorCodes.ExportExpired, message),
                (download.StatusCode, problem.RootElement.GetProperty("errorCode").GetString(), problem.RootElement.GetProperty("detail").GetString()));
        }

        Assert.Equal("PK"u8.ToArray(), (await DownloadAsync(manager, kept))[..2]);

        // A second round finds nothing of these any more; the first deletion time stays.
        var deletedAt = (await ExportAsync(factory, expired)).DeletedAt;
        await SweepAsync(host);
        Assert.Equal(deletedAt, (await ExportAsync(factory, expired)).DeletedAt);
    }

    [Fact]
    public async Task While_the_storage_is_down_the_sweep_marks_nothing_and_finishes_in_a_later_round()
    {
        var (manager, _, _) = await factory.CreateUserClientAsync(AppRoles.Manager);
        var taskId = await RequestInventoryAsync(manager, $"none-{TestData.UniqueSuffix()}");
        using (var workers = await StartWorkersAsync(factory, broker, "export-e2e-sweep-outage"))
        {
            await WaitForStatusAsync(factory, taskId, ProcessingTaskStatus.Succeeded);
            await workers.StopAsync();
        }

        await ExpireAsync(factory, taskId, TimeSpan.FromSeconds(30));
        var logs = new CapturingLoggerProvider();

        await _storage.StopAsync();
        try
        {
            // The hosted sweep itself, every second.
            using var workers = await StartWorkersAsync(
                factory,
                broker,
                "export-e2e-sweep-outage",
                new Dictionary<string, string?> { ["Reports:CleanupInterval"] = "00:00:01" },
                services => services.CaptureLogs(logs),
                retentionSweep: true);
            try
            {
                // Two rounds tried and failed for this export.
                await Eventually.WaitAsync(
                    () => Task.FromResult(logs.Entries.Count(e =>
                        e.Level == LogLevel.Warning && e.Message.Contains($"expired export {taskId} could not be deleted", StringComparison.Ordinal))),
                    count => count >= 2,
                    WaitTimeout,
                    "two sweep rounds while the storage is down");
                Assert.Null((await ExportAsync(factory, taskId)).DeletedAt);
                // For its requester the export is expired all the same: its retention time is over.
                Assert.Equal(ReportExportStatus.Expired, (await DetailAsync(manager, taskId)).Status);

                await _storage.StartAsync();
                await Eventually.WaitAsync(
                    () => ExportAsync(factory, taskId), export => export.DeletedAt is not null, WaitTimeout, "the export is expired once the storage is back");
            }
            finally
            {
                await workers.StopAsync();
            }
        }
        finally
        {
            await _storage.StartAsync();
        }

        Assert.False(await _storage.ExistsAsync(ReportExport.ObjectKeyOf(taskId)));
        var export = await ExportAsync(factory, taskId);
        Assert.Equal(ReportExportCleanupService.Actor, export.UpdatedBy);
        Assert.Equal(ReportExportStatus.Expired, (await DetailAsync(manager, taskId)).Status);
        // The warning names the export, never the storage.
        Assert.All(logs.Entries.Select(e => e.Message), text => Assert.DoesNotContain(_storage.ServiceUrl, text, StringComparison.OrdinalIgnoreCase));
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    /// <summary>The file of a succeeded export, read with its download link by a client that has no token of the API.</summary>
    private async Task<byte[]> DownloadAsync(HttpClient client, Guid taskId)
    {
        var response = await client.GetAsync($"/api/reports/exports/{taskId}/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var file = await _browser.GetAsync((await response.ReadAsync<ReportDownloadResponse>()).Url);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        return await file.Content.ReadAsByteArrayAsync();
    }

    private static async Task<ReportExportCleanupResult> SweepAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ReportExportCleanup>().RunAsync(CancellationToken.None);
    }

    private Task<Alert> AlertOfAsync(Guid taskId) =>
        factory.QueryDbAsync(db => db.Alerts.AsNoTracking()
            .Where(a => a.DedupKey == AlertDedupKey.ProcessingFailed(taskId))
            .OrderByDescending(a => a.LastOccurredAt)
            .FirstAsync());

    private static byte[] Envelope(string messageId, Guid taskId) =>
        MessageJson.SerializeToUtf8Bytes(new MessageEnvelope(
            messageId,
            $"corr-{messageId}",
            taskId,
            EventTypes.ReportExportRequested,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new ReportExportRequestedPayload(taskId), MessageJson.Options)));

    /// <summary>Publishes a raw message to the export queue, as the Outbox Publisher would.</summary>
    private async Task PublishAsync(byte[] body, string messageId)
    {
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var properties = new BasicProperties { MessageId = messageId, ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent };
        await channel.BasicPublishAsync(RabbitMqTopology.Exchange, EventTypes.ReportExportRequested, mandatory: true, properties, body);
    }

    /// <summary>Drains the export DLQ until <paramref name="messageId"/> was seen.</summary>
    private async Task WaitForDeadLetterAsync(string messageId)
    {
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await Eventually.WaitAsync(
            async () =>
            {
                while (await channel.BasicGetAsync(DeadLetterQueue, autoAck: true) is { } message)
                {
                    _deadLettered.Add(message.BasicProperties.MessageId);
                }

                return _deadLettered.Contains(messageId);
            },
            found => found,
            WaitTimeout,
            $"message {messageId} in {DeadLetterQueue}");
    }
}
