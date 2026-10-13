using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Tests.Integration.Workers;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// What the export tests share: a Workers host against the API's test database and storage emulator, seeded orders
/// and products, and reading a downloaded workbook.
/// </summary>
internal static class ReportExportTesting
{
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// A started Workers host on the API test database, the given broker and the API's storage emulator. It also works
    /// off what other tests left behind in that database (order tasks, exports nobody built yet).
    /// </summary>
    public static async Task<IHost> StartWorkersAsync(
        ApiFactory factory,
        RabbitMqBrokerFixture broker,
        string instanceId,
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null,
        bool retentionSweep = false)
    {
        var host = await BuildWorkersAsync(factory, broker.HostName, broker.Port, instanceId, overrides, configureServices, retentionSweep);
        await host.StartAsync();
        return host;
    }

    /// <summary>A Workers host with the storage of the API tests (not started: for its services, or to be started by the test).</summary>
    public static async Task<IHost> BuildWorkersAsync(
        ApiFactory factory,
        string brokerHostName,
        int brokerPort,
        string instanceId,
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null,
        bool retentionSweep = false)
    {
        var storage = await factory.StorageAsync();
        var settings = new Dictionary<string, string?>
        {
            ["ObjectStorage:ServiceUrl"] = storage.ServiceUrl,
            ["ObjectStorage:Region"] = StorageEmulator.Region,
            ["ObjectStorage:ForcePathStyle"] = "true",
            ["ObjectStorage:BucketName"] = StorageEmulator.Bucket,
            ["ObjectStorage:AccessKeyId"] = storage.AccessKeyId,
            ["ObjectStorage:SecretAccessKey"] = storage.SecretAccessKey,
            ["ObjectStorage:RequestTimeout"] = ApiFactory.StorageRequestTimeout.ToString("c")
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        return WorkersFixture.BuildWorkersHost(
            factory.DatabaseConnectionString, brokerHostName, brokerPort, instanceId, settings, configureServices, retentionSweep);
    }

    // ---- requests ----------------------------------------------------------------------------------------------

    public static async Task<Guid> RequestAsync(HttpClient client, string path, object request, string? language = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(request, options: TestData.Json) };
        if (language is not null)
        {
            message.Headers.AcceptLanguage.ParseAdd(language);
        }

        var response = await client.SendAsync(message);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{path}: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.ReadAsync<ExportAcceptedResponse>()).TaskId;
    }

    public static Task<Guid> RequestInventoryAsync(HttpClient client, string search, string timeZone = "Asia/Ho_Chi_Minh", string? language = null) =>
        RequestAsync(client, "/api/reports/inventory/export", new ExportInventoryRequest(search, false, timeZone), language);

    public static async Task<ReportExportResponse> DetailAsync(HttpClient client, Guid taskId)
    {
        var response = await client.GetAsync($"/api/reports/exports/{taskId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<ReportExportResponse>();
    }

    // ---- the database ------------------------------------------------------------------------------------------

    public static Task<ProcessingTask> TaskAsync(ApiFactory factory, Guid taskId) =>
        factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId));

    public static Task<ProcessingTask> WaitForTaskAsync(ApiFactory factory, Guid taskId, Func<ProcessingTask, bool> done, string what) =>
        Eventually.WaitAsync(() => TaskAsync(factory, taskId), done, WaitTimeout, $"export task {taskId}: {what}");

    public static Task<ProcessingTask> WaitForStatusAsync(ApiFactory factory, Guid taskId, ProcessingTaskStatus status) =>
        WaitForTaskAsync(factory, taskId, t => t.Status == status, status.ToString());

    public static Task<ReportExport> ExportAsync(ApiFactory factory, Guid taskId) =>
        factory.QueryDbAsync(db => db.ReportExports.AsNoTracking().SingleAsync(e => e.TaskId == taskId));

    /// <summary>Moves the end of an export's retention time (the only way a test gets an expired export).</summary>
    public static Task ExpireAsync(ApiFactory factory, Guid taskId, TimeSpan ago) =>
        SetExpiryAsync(factory, taskId, DateTimeOffset.UtcNow - ago);

    /// <summary>Sets the end of an export's retention time; nothing else of the export changes.</summary>
    public static Task SetExpiryAsync(ApiFactory factory, Guid taskId, DateTimeOffset expiresAt) =>
        factory.QueryDbAsync(db => db.ReportExports
            .Where(e => e.TaskId == taskId)
            .ExecuteUpdateAsync(update => update.SetProperty(e => e.ExpiresAt, expiresAt.ToUniversalTime())));

    /// <summary>Products with their inventory rows, stored directly (no import: only the rows matter here).</summary>
    public static async Task<List<Product>> SeedProductsAsync(
        ApiFactory factory, params (string Sku, string Name, decimal Price, int Threshold, int Total, DateOnly? Expiry)[] products)
    {
        var entities = products.Select(p => new Product(p.Sku, p.Name, p.Price, p.Threshold, p.Expiry)).ToList();
        await factory.QueryDbAsync(async db =>
        {
            db.Products.AddRange(entities);
            db.Inventories.AddRange(entities.Zip(products, (entity, p) => new Inventory(entity.ProductId, p.Total)));
            return await db.SaveChangesAsync();
        });
        return entities;
    }

    /// <summary>An order with the given creation time (the audit stamp is replaced afterwards).</summary>
    public static async Task<Order> SeedOrderAsync(
        ApiFactory factory,
        string channelSlug,
        Guid customerId,
        string externalOrderId,
        OrderStatus status,
        DateTimeOffset createdAt,
        params (Guid ProductId, int Quantity, decimal UnitPrice)[] items)
    {
        return await factory.QueryDbAsync(async db =>
        {
            var channelId = await db.Channels.Where(c => c.Slug == channelSlug).Select(c => c.ChannelId).SingleAsync();
            var order = new Order(channelId, customerId, externalOrderId, status);
            foreach (var (productId, quantity, unitPrice) in items)
            {
                order.AddItem(productId, quantity, unitPrice);
            }

            db.Orders.Add(order);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"""UPDATE "Orders" SET "CreatedAt" = {createdAt.ToUniversalTime()} WHERE "OrderId" = {order.OrderId}""");
            return order;
        });
    }

    public static async Task<Customer> SeedCustomerAsync(ApiFactory factory, string name, string? phone = null)
    {
        var customer = new Customer(name, phone, "private@customer.test", "1 Private Street");
        await factory.QueryDbAsync(async db =>
        {
            db.Customers.Add(customer);
            return await db.SaveChangesAsync();
        });
        return customer;
    }

    // ---- the file ----------------------------------------------------------------------------------------------

    /// <summary>The cells of a sheet's row as text, for whole-row comparisons.</summary>
    public static string[] RowTexts(IXLWorksheet sheet, int row, int columns) =>
        Enumerable.Range(1, columns).Select(column => sheet.Cell(row, column).GetString()).ToArray();

    public static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));
}

/// <summary>
/// Test seam of a Workers host: counts what is stored per key, and can make the first completion of an export fail
/// after its file was stored (the crash between upload and commit).
/// </summary>
internal sealed class ExportProbe
{
    private readonly ConcurrentDictionary<string, int> _puts = new(StringComparer.Ordinal);
    private int _completionFailures;

    /// <summary>Failing <c>CompleteAsync</c> calls for <see cref="OnlyTaskId"/> (the file is stored by then).</summary>
    public int CompletionFailures { get; init; }

    public Guid? OnlyTaskId { get; init; }

    public int PutsOf(Guid taskId) => _puts.GetValueOrDefault(ReportExport.ObjectKeyOf(taskId));

    internal void Stored(string key) => _puts.AddOrUpdate(key, 1, (_, count) => count + 1);

    internal bool NextCompletionFails(Guid taskId) =>
        (OnlyTaskId is not { } only || only == taskId) && Interlocked.Increment(ref _completionFailures) <= CompletionFailures;
}

internal static class ExportProbeExtensions
{
    public static IServiceCollection ProbeExports(this IServiceCollection services, ExportProbe probe)
    {
        var storage = services.Single(d => d.ServiceType == typeof(IObjectStorage));
        services.Remove(storage);
        services.AddSingleton<IObjectStorage>(sp => new CountingStorage(
            (IObjectStorage)ActivatorUtilities.CreateInstance(sp, storage.ImplementationType!), probe));

        var processing = services.Single(d => d.ServiceType == typeof(IReportExportProcessingService));
        services.Remove(processing);
        services.AddScoped<IReportExportProcessingService>(sp => new FailingProcessing(
            (IReportExportProcessingService)ActivatorUtilities.CreateInstance(sp, processing.ImplementationType!), probe));

        return services;
    }

    public static IServiceCollection CaptureLogs(this IServiceCollection services, CapturingLoggerProvider logs)
    {
        services.AddSingleton<ILoggerProvider>(logs);
        return services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug));
    }

    private sealed class CountingStorage(IObjectStorage inner, ExportProbe probe) : IObjectStorage
    {
        public PresignedUrl PresignPut(string key, string contentType, TimeSpan lifetime) => inner.PresignPut(key, contentType, lifetime);

        public PresignedUrl PresignGet(string key, TimeSpan lifetime) => inner.PresignGet(key, lifetime);

        public Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken) => inner.HeadAsync(key, cancellationToken);

        public Task<byte[]> ReadStartAsync(string key, int count, CancellationToken cancellationToken) => inner.ReadStartAsync(key, count, cancellationToken);

        public async Task PutAsync(string key, Stream content, string contentType, string contentDisposition, CancellationToken cancellationToken)
        {
            await inner.PutAsync(key, content, contentType, contentDisposition, cancellationToken);
            probe.Stored(key);
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
    }

    private sealed class FailingProcessing(IReportExportProcessingService inner, ExportProbe probe) : IReportExportProcessingService
    {
        public Task<ReportExportTaskState?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) => inner.GetTaskAsync(taskId, cancellationToken);

        public Task<AttemptStart?> StartAttemptAsync(Guid taskId, string messageId, string workerName, string workerInstanceId, CancellationToken cancellationToken) =>
            inner.StartAttemptAsync(taskId, messageId, workerName, workerInstanceId, cancellationToken);

        public Task<GeneratedReport> GenerateAsync(Guid taskId, CancellationToken cancellationToken) => inner.GenerateAsync(taskId, cancellationToken);

        public Task CompleteAsync(Guid taskId, Guid attemptId, long sizeBytes, int rowCount, CancellationToken cancellationToken) =>
            probe.NextCompletionFails(taskId)
                ? throw new TimeoutException("injected failure after the upload, before the commit")
                : inner.CompleteAsync(taskId, attemptId, sizeBytes, rowCount, cancellationToken);

        public Task RecordTransientFailureAsync(Guid taskId, Guid attemptId, string errorCode, string errorMessage, CancellationToken cancellationToken) =>
            inner.RecordTransientFailureAsync(taskId, attemptId, errorCode, errorMessage, cancellationToken);

        public Task<int?> RecordFinalFailureAsync(
            Guid taskId, Guid? attemptId, string errorCode, string errorMessage, string workerName, string workerInstanceId, string queue,
            CancellationToken cancellationToken) =>
            inner.RecordFinalFailureAsync(taskId, attemptId, errorCode, errorMessage, workerName, workerInstanceId, queue, cancellationToken);

        public Task RecordRejectedMessageAsync(
            string? messageId, string? correlationId, Guid? taskId, MessageRejection rejection, string workerName, CancellationToken cancellationToken) =>
            inner.RecordRejectedMessageAsync(messageId, correlationId, taskId, rejection, workerName, cancellationToken);
    }
}
