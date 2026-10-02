using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 008 acceptance: the order/task read services load nested data without N+1 queries. Counts the SQL commands EF Core
/// sends (runs in the Workers collection because only there a host with an extra DbContext interceptor can be built).
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed class QueryShapeTests(WorkersFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Order_detail_is_one_statement_and_lists_are_count_plus_page()
    {
        var correlationId = $"shape-{Guid.NewGuid():N}";
        var orderId = await SeedAsync(correlationId);
        var commands = new CommandCounter();
        using var host = fixture.CreateHost(
            "query-shape",
            configureServices: services => services.ConfigureDbContext<OmnichannelDbContext>(o => o.AddInterceptors(commands)));

        await using var scope = host.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<IOrderQueryService>();
        var tasks = scope.ServiceProvider.GetRequiredService<IProcessingTaskService>();

        // Detail: lines with products, two tasks with three and two attempts - one statement.
        var detail = await commands.CountAsync(() => orders.GetAsync(orderId, CancellationToken.None), expected: 1);
        Assert.Equal(3, detail.Items.Count);
        Assert.Equal([2, 3], detail.Tasks.Select(t => t.Attempts.Count).Order());

        // Lists: COUNT + page, whatever the number of rows and attempts.
        var orderPage = await commands.CountAsync(
            () => orders.ListAsync(new OrderListQuery { CustomerId = detail.Customer.CustomerId }, CancellationToken.None), expected: 2);
        Assert.Equal(3, Assert.Single(orderPage.Items).ItemCount);

        var taskPage = await commands.CountAsync(
            () => tasks.ListAsync(new ProcessingTaskListQuery { CorrelationId = correlationId, WorkerName = "OrderWorker" }, CancellationToken.None),
            expected: 2);
        Assert.Equal(7, taskPage.TotalCount); // the order task + six more with attempts on OrderWorker
        Assert.All(taskPage.Items, t => Assert.Equal(t.AttemptCount, t.LastAttempt!.AttemptNo));

        // Task detail: task + attempts, then the outbox row by primary key.
        var orderTaskId = detail.Tasks.Single(t => t.TaskType == ProcessingTaskType.OrderProcessing).TaskId;
        var taskDetail = await commands.CountAsync(() => tasks.GetAsync(orderTaskId, CancellationToken.None), expected: 2);
        Assert.Equal(3, taskDetail.Attempts.Count);
    }

    private async Task<Guid> SeedAsync(string correlationId) =>
        await fixture.WithDbAsync(async db =>
        {
            var channel = await db.Channels.SingleAsync(c => c.Slug == "website");
            var customer = new Customer($"Shape customer {Guid.NewGuid():N}", null, null, null);
            var products = Enumerable.Range(0, 3).Select(n => new Product($"SHAPE-{Guid.NewGuid():N}"[..20].ToUpperInvariant(), $"Shape {n}", 1m + n, 0, null)).ToList();
            var order = new Order(channel.ChannelId, customer.CustomerId, $"SHAPE-{Guid.NewGuid():N}", OrderStatus.Reserved);
            products.ForEach(p => order.AddItem(p.ProductId, 2, p.Price));

            var orderTask = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, correlationId, Guid.NewGuid().ToString());
            AddFailures(orderTask, 2);
            orderTask.Succeed(order.OrderId, orderTask.StartAttempt("OrderWorker", "i-1", T0).AttemptId, T0);

            var invoiceTask = new ProcessingTask(ProcessingTaskType.Invoice, order.OrderId, correlationId, null);
            var invoice = invoiceTask.StartAttempt("InvoiceWorker", "i-2", T0);
            invoiceTask.ScheduleRetry(invoice.AttemptId, "timeout", "slow", T0);
            invoiceTask.StartAttempt("InvoiceWorker", "i-2", T0);

            var others = Enumerable.Range(1, 6).Select(n =>
            {
                var task = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, correlationId, null);
                AddFailures(task, n);
                return task;
            });

            db.Customers.Add(customer);
            db.Products.AddRange(products);
            db.Orders.Add(order);
            db.ProcessingTasks.AddRange([orderTask, invoiceTask, .. others]);
            await db.SaveChangesAsync();
            return order.OrderId;
        });

    private static void AddFailures(ProcessingTask task, int count)
    {
        for (var n = 0; n < count; n++)
        {
            task.ScheduleRetry(task.StartAttempt("OrderWorker", "i-1", T0).AttemptId, "timeout", "slow", T0);
        }
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public async Task<T> CountAsync<T>(Func<Task<T>> action, int expected)
        {
            Interlocked.Exchange(ref _count, 0);
            var result = await action();
            Assert.Equal(expected, Volatile.Read(ref _count));
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
