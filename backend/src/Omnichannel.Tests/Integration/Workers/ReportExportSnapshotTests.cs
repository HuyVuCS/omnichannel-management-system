using System.Data.Common;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.Persistence.Reports;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 020 C4 and C6, against real PostgreSQL without broker or storage: the rows of an export come from one snapshot,
/// and the retention sweep selects exactly the exports whose time is over and whose task has ended.
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReportExportSnapshotTests(WorkersFixture fixture)
{
    [Fact]
    public async Task An_order_committed_between_the_two_queries_of_an_export_is_in_neither_sheet()
    {
        var token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var (channelId, customerId, productId) = await SeedCatalogAsync(token);
        await AddOrderAsync(channelId, customerId, productId, $"SNAP-{token}-1");
        await AddOrderAsync(channelId, customerId, productId, $"SNAP-{token}-2");
        var taskId = await AddExportAsync(MessageJson.Serialize(new OrdersExportParameters(
            null, null, null, $"SNAP-{token}", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), "UTC", "en")));

        // Right before the worker reads the item lines, another connection commits one more matching order.
        var hook = new BeforeItemsQuery(() => AddOrderAsync(channelId, customerId, productId, $"SNAP-{token}-3"));
        using var host = fixture.CreateHost(
            "export-snapshot", configureServices: services => services.ConfigureDbContext<OmnichannelDbContext>(o => o.AddInterceptors(hook)));
        await using var scope = host.Services.CreateAsyncScope();

        using var report = await scope.ServiceProvider.GetRequiredService<IReportExportProcessingService>().GenerateAsync(taskId, CancellationToken.None);

        // The order was committed while the file was being built ...
        Assert.Equal(1, hook.Fired);
        Assert.Equal(3, await fixture.WithDbAsync(db => db.Orders.CountAsync(o => o.ExternalOrderId.StartsWith($"SNAP-{token}"))));
        // ... and it is in neither sheet: orders and item lines are read from the same snapshot.
        using var workbook = new XLWorkbook(report.Content);
        var orders = workbook.Worksheet("Orders");
        var items = workbook.Worksheet("Items");
        Assert.Equal([$"SNAP-{token}-1", $"SNAP-{token}-2"], new[] { 2, 3 }.Select(row => orders.Cell(row, 3).GetString()).Order());
        Assert.Equal([$"SNAP-{token}-1", $"SNAP-{token}-2"], new[] { 2, 3 }.Select(row => items.Cell(row, 2).GetString()).Order());
        Assert.Equal((3, 3, 4), (orders.LastRowUsed()!.RowNumber(), items.LastRowUsed()!.RowNumber(), report.RowCount));
        Assert.Equal((2d, 2d), (workbook.Worksheet("Info").Cell(11, 2).GetDouble(), workbook.Worksheet("Info").Cell(12, 2).GetDouble()));

        // The transaction of the read: REPEATABLE READ and READ ONLY, set before the first query.
        Assert.Equal(System.Data.IsolationLevel.RepeatableRead, hook.IsolationLevel);
        Assert.StartsWith("SET TRANSACTION READ ONLY", hook.FirstCommandInTransaction, StringComparison.Ordinal);

        // The same export built again now has the third order in both sheets.
        using var again = await scope.ServiceProvider.GetRequiredService<IReportExportProcessingService>().GenerateAsync(taskId, CancellationToken.None);
        Assert.Equal(6, again.RowCount);
    }

    [Fact]
    public async Task The_read_transaction_of_an_export_cannot_write()
    {
        using var host = fixture.CreateHost("export-read-only");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

        // What the statement the export starts with means to PostgreSQL: a write in that transaction is refused (25006).
        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("""UPDATE "Channels" SET "Name" = "Name" WHERE FALSE"""));
        Assert.Equal(Npgsql.PostgresErrorCodes.ReadOnlySqlTransaction, refused.SqlState);
    }

    [Fact]
    public async Task The_sweep_selects_expired_exports_whose_task_has_ended_oldest_first()
    {
        var now = DateTimeOffset.UtcNow;
        // Far in the past, so these are the oldest expired exports of this database whatever other tests added.
        var epoch = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(Random.Shared.Next(0, 1_000_000));
        var succeeded = await AddExportAsync("{}", epoch, task => Finish(task, fail: false), export => export.Complete(10, 1, now));
        var failed = await AddExportAsync("{}", epoch.AddSeconds(1), task => Finish(task, fail: true));
        var pending = await AddExportAsync("{}", epoch.AddSeconds(2));
        var running = await AddExportAsync("{}", epoch.AddSeconds(3), task => task.StartAttempt("ReportExportWorker", "i-1", now));
        var retrying = await AddExportAsync(
            "{}", epoch.AddSeconds(4), task => task.ScheduleRetry(task.StartAttempt("ReportExportWorker", "i-1", now).AttemptId, "storage_unavailable", "down", now));
        var notYet = await AddExportAsync("{}", now.AddMinutes(1), task => Finish(task, fail: false), export => export.Complete(10, 1, now));
        var deleted = await AddExportAsync("{}", epoch.AddSeconds(5), task => Finish(task, fail: false), export =>
        {
            export.Complete(10, 1, now);
            export.MarkDeleted(now);
        });
        var alsoFailed = await AddExportAsync("{}", epoch.AddSeconds(6), task => Finish(task, fail: true));

        using var host = fixture.CreateHost("export-sweep-selection");
        await using var scope = host.Services.CreateAsyncScope();
        var cleanup = scope.ServiceProvider.GetRequiredService<ReportExportCleanup>();
        var mine = new[] { succeeded, failed, pending, running, retrying, notYet, deleted, alsoFailed };

        var all = await cleanup.DueAsync(1000, CancellationToken.None);
        var firstTwo = await cleanup.DueAsync(2, CancellationToken.None);

        // Ended (succeeded or failed), past its time, file not deleted yet - in the order of their expiry.
        Assert.Equal([succeeded, failed, alsoFailed], all.Where(mine.Contains));
        Assert.Equal([succeeded, failed], firstTwo.Where(mine.Contains));
        Assert.True(firstTwo.Count <= 2);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static void Finish(ProcessingTask task, bool fail)
    {
        var now = DateTimeOffset.UtcNow;
        var attempt = task.StartAttempt("ReportExportWorker", "i-1", now);
        if (fail)
        {
            task.Fail(attempt.AttemptId, "storage_unavailable", "down", now);
        }
        else
        {
            task.Succeed(null, attempt.AttemptId, now);
        }
    }

    private Task<Guid> AddExportAsync(
        string parameters, DateTimeOffset? expiresAt = null, Action<ProcessingTask>? shapeTask = null, Action<ReportExport>? shapeExport = null) =>
        fixture.WithDbAsync(async db =>
        {
            var task = new ProcessingTask(ProcessingTaskType.ReportExport, null, $"export-{Guid.NewGuid():N}", null);
            shapeTask?.Invoke(task);
            var export = new ReportExport(task.TaskId, ReportType.Orders, parameters, "orders.xlsx", expiresAt ?? DateTimeOffset.UtcNow.AddDays(7));
            shapeExport?.Invoke(export);
            db.ProcessingTasks.Add(task);
            db.ReportExports.Add(export);
            await db.SaveChangesAsync();
            return task.TaskId;
        });

    private Task<(Guid ChannelId, Guid CustomerId, Guid ProductId)> SeedCatalogAsync(string token) =>
        fixture.WithDbAsync(async db =>
        {
            var channelId = await db.Channels.Where(c => c.Slug == "website").Select(c => c.ChannelId).SingleAsync();
            var customer = new Customer($"Snapshot {token}", null, null, null);
            var product = new Product($"SNAP-{token}", "Snapshot product", 10m, 0, null);
            db.Customers.Add(customer);
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return (channelId, customer.CustomerId, product.ProductId);
        });

    private Task<Guid> AddOrderAsync(Guid channelId, Guid customerId, Guid productId, string externalOrderId) =>
        fixture.WithDbAsync(async db =>
        {
            var order = new Order(channelId, customerId, externalOrderId, OrderStatus.Reserved);
            order.AddItem(productId, 1, 10m);
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return order.OrderId;
        });

    /// <summary>
    /// Runs <paramref name="action"/> once, right before the query that reads the item lines of an export is sent (the
    /// first statement over <c>OrderItems</c> that is not the count of an order's lines), and records what the read
    /// transaction looked like.
    /// </summary>
    private sealed class BeforeItemsQuery(Func<Task> action) : DbCommandInterceptor
    {
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public System.Data.IsolationLevel? IsolationLevel { get; private set; }

        public string? FirstCommandInTransaction { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            var readsItemLines = command.Transaction is not null
                && command.CommandText.Contains("JOIN \"OrderItems\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"UnitPrice\"", StringComparison.Ordinal);
            if (readsItemLines && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await action();
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            if (command.Transaction is { } transaction && FirstCommandInTransaction is null)
            {
                IsolationLevel = transaction.IsolationLevel;
                FirstCommandInTransaction = command.CommandText;
            }
        }
    }
}

/// <summary>
/// Task 020 B1/B2: the Workers host refuses to start with a wrong <c>Reports</c> or worker setting, or without the keys
/// of the object storage - before any worker runs.
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReportExportHostOptionsTests(WorkersFixture fixture)
{
    [Theory]
    [InlineData("Reports:MaxRows", "0", "Reports:MaxRows must be between 1 and 1000000.")]
    [InlineData("Reports:Retention", "00:00:10", "Reports:Retention must be between 00:01:00 and 366.00:00:00.")]
    [InlineData("Reports:CleanupBatchSize", "5000", "Reports:CleanupBatchSize must be between 1 and 1000.")]
    [InlineData("ReportExportWorker:Concurrency", "0", "ReportExportWorker:Concurrency must be between 1 and 8.")]
    [InlineData("ReportExportWorker:MaxAttempts", "21", "ReportExportWorker:MaxAttempts must be between 1 and 20.")]
    [InlineData("ObjectStorage:AccessKeyId", "", "ObjectStorage:AccessKeyId is required (environment variable or user-secrets).")]
    [InlineData("ObjectStorage:SecretAccessKey", "", "ObjectStorage:SecretAccessKey is required (environment variable or user-secrets).")]
    [InlineData("ObjectStorage:BucketName", " ", "ObjectStorage:BucketName is required.")]
    public async Task The_workers_host_does_not_start_with_a_setting_that_is_missing_or_out_of_range(string setting, string value, string message)
    {
        using var host = fixture.CreateHost("export-options", new Dictionary<string, string?> { [setting] = value });

        var failure = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => host.StartAsync());

        Assert.Equal([message], failure.Failures);
    }
}
