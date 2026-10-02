using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Realtime;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 015: when a hint is delivered, seen from a separate <c>LISTEN</c> connection of the running database (not read from
/// the documentation): after the commit, never after a rollback or a failed save, one notification per unit of work.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RealtimeNotifyTests(ApiFactory factory)
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(700);

    [Fact]
    public async Task Save_without_a_transaction_notifies_after_its_own_commit_with_the_documented_payload()
    {
        await using var probe = await HintProbe.StartAsync(factory.DatabaseConnectionString);
        var task = new ProcessingTask(ProcessingTaskType.Invoice, null, $"hint-{TestData.UniqueSuffix()}", null);
        // At the moment the notification arrives, another connection must already see the row.
        probe.Check = () => CountTasks(task.TaskId) == 1;

        await SaveAsync(db => db.ProcessingTasks.Add(task));

        var notification = await probe.WaitForTaskAsync(task.TaskId);
        Assert.Equal(
            $$"""{"events":[{"type":"processing.task.changed","taskId":"{{task.TaskId}}","taskType":"Invoice","status":"Pending"}]}""",
            notification.Payload);
        Assert.True(notification.CheckResult);
        Assert.Single(probe.Notifications, n => n.HasTask(task.TaskId));
    }

    [Fact]
    public async Task Save_inside_a_transaction_is_delivered_at_the_commit_not_before()
    {
        await using var probe = await HintProbe.StartAsync(factory.DatabaseConnectionString);
        var task = new ProcessingTask(ProcessingTaskType.Invoice, null, $"hint-{TestData.UniqueSuffix()}", null);
        probe.Check = () => CountTasks(task.TaskId) == 1;

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.ProcessingTasks.Add(task);
            await db.SaveChangesAsync();

            // Saved, pg_notify executed, transaction still open: nothing is delivered and nobody else sees the row.
            await Task.Delay(Quiet);
            Assert.DoesNotContain(probe.Notifications, n => n.HasTask(task.TaskId));
            Assert.Equal(0, CountTasks(task.TaskId));

            await transaction.CommitAsync();
        }

        var notification = await probe.WaitForTaskAsync(task.TaskId);
        Assert.True(notification.CheckResult);
        Assert.Single(probe.Notifications, n => n.HasTask(task.TaskId));
    }

    [Fact]
    public async Task Rolled_back_transaction_delivers_nothing()
    {
        await using var probe = await HintProbe.StartAsync(factory.DatabaseConnectionString);
        var rolledBack = new ProcessingTask(ProcessingTaskType.Invoice, null, $"hint-{TestData.UniqueSuffix()}", null);
        var sentinel = new ProcessingTask(ProcessingTaskType.Invoice, null, $"hint-{TestData.UniqueSuffix()}", null);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.ProcessingTasks.Add(rolledBack);
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        // A later unit of work is delivered; the rolled-back one never is.
        await SaveAsync(db => db.ProcessingTasks.Add(sentinel));
        await probe.WaitForTaskAsync(sentinel.TaskId);
        await Task.Delay(Quiet);

        Assert.DoesNotContain(probe.Notifications, n => n.HasTask(rolledBack.TaskId));
        Assert.Equal(0, CountTasks(rolledBack.TaskId));
    }

    [Fact]
    public async Task Failed_save_sends_nothing_and_its_hints_are_not_carried_into_the_next_save()
    {
        await using var probe = await HintProbe.StartAsync(factory.DatabaseConnectionString);
        var key = $"hint-key-{TestData.UniqueSuffix()}";
        var existing = new ProcessingTask(ProcessingTaskType.Invoice, null, "hint-corr", null, key);
        await SaveAsync(db => db.ProcessingTasks.Add(existing));
        await probe.WaitForTaskAsync(existing.TaskId);

        var duplicate = new ProcessingTask(ProcessingTaskType.Invoice, null, "hint-corr", null, key);
        var next = new ProcessingTask(ProcessingTaskType.Invoice, null, "hint-corr", null);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // One scope = one interceptor instance: the failed save and the next one share it.
            var db = scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>();
            db.ProcessingTasks.Add(duplicate);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            db.ChangeTracker.Clear();
            db.ProcessingTasks.Add(next);
            await db.SaveChangesAsync();
        }

        var notification = await probe.WaitForTaskAsync(next.TaskId);
        Assert.Equal(next.TaskId, Assert.Single(notification.Events).TaskId);
        await Task.Delay(Quiet);
        Assert.DoesNotContain(probe.Notifications, n => n.HasTask(duplicate.TaskId));
    }

    [Fact]
    public async Task Save_without_anything_of_interest_sends_no_notification()
    {
        await using var probe = await HintProbe.StartAsync(factory.DatabaseConnectionString);
        var sentinel = new ProcessingTask(ProcessingTaskType.Invoice, null, $"hint-{TestData.UniqueSuffix()}", null);

        await SaveAsync(db => db.Customers.Add(new Customer($"Hint customer {TestData.UniqueSuffix()}", null, null, null)));
        await SaveAsync(db => db.ProcessingTasks.Add(sentinel));

        await probe.WaitForTaskAsync(sentinel.TaskId);
        Assert.Equal(sentinel.TaskId, Assert.Single(Assert.Single(probe.Notifications).Events).TaskId);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(200, true)]
    public async Task Stock_import_is_one_notification_whose_product_list_is_capped(int lines, bool expectAll)
    {
        var manager = factory.CreateClientAs(AppRoles.Manager);
        var products = Enumerable.Range(0, lines)
            .Select(n => new Product($"HINT-{TestData.UniqueSuffix()}-{n}".ToUpperInvariant(), "Hint product", 1m, 0, null))
            .ToList();
        await SaveAsync(db =>
        {
            db.Products.AddRange(products);
            db.Inventories.AddRange(products.Select(p => new Inventory(p.ProductId, 0)));
        });
        await using var probe = await HintProbe.StartAsync(factory.DatabaseConnectionString);

        var response = await manager.PostAsJsonAsync(
            "/api/inventory/import",
            new ImportInventoryRequest($"GRN-{TestData.UniqueSuffix()}", null, products.Select(p => new ImportInventoryItem(p.ProductId, 1)).ToList()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var all = await probe.WaitForAsync(n => n.Any(x => x.Events.Any(e => e.Type == RealtimeEventTypes.InventoryChanged)), "inventory.changed");
        await Task.Delay(Quiet);

        // 200 movements, 200 outbox rows and the audit row are one unit of work: one notification, one event.
        var notification = Assert.Single(probe.Notifications);
        var hint = Assert.Single(notification.Events);
        Assert.Equal(RealtimeEventTypes.InventoryChanged, hint.Type);
        Assert.True(notification.PayloadBytes < 8000, $"payload has {notification.PayloadBytes} bytes");
        if (expectAll)
        {
            Assert.Equal(((bool?)true, (IReadOnlyList<Guid>?)null), (hint.All, hint.ProductIds));
            Assert.Equal("""{"events":[{"type":"inventory.changed","all":true}]}""", notification.Payload);
        }
        else
        {
            Assert.Null(hint.All);
            Assert.Equal(products.Select(p => p.ProductId).Order(), hint.ProductIds!.Order());
        }

        Assert.Single(all);
    }

    [Fact]
    public async Task Listening_needs_no_privilege_beyond_connecting_to_the_database()
    {
        // A role without any right on any table (the application role has only DML rights; LISTEN is not a grantable right).
        const string role = "hint_listener_test";
        var password = $"pw-{Guid.NewGuid():N}";
        await using (var superuser = new NpgsqlConnection(factory.DatabaseConnectionString))
        {
            await superuser.OpenAsync();
            await using var create = new NpgsqlCommand(
                $"DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{role}') THEN CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE; END IF; END $$; "
                + $"ALTER ROLE {role} PASSWORD '{password}'",
                superuser);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(factory.DatabaseConnectionString) { Username = role, Password = password }.ConnectionString;

        await using var probe = await HintProbe.StartAsync(connectionString);
        var task = new ProcessingTask(ProcessingTaskType.Invoice, null, $"hint-{TestData.UniqueSuffix()}", null);
        await SaveAsync(db => db.ProcessingTasks.Add(task));

        await probe.WaitForTaskAsync(task.TaskId);

        // ... and the role really cannot read the data the hint points to.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var select = new NpgsqlCommand("SELECT count(*) FROM \"ProcessingTasks\"", connection);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>One unit of work through the application's DbContext (with its interceptors), no explicit transaction.</summary>
    private Task SaveAsync(Action<OmnichannelDbContext> change) =>
        factory.QueryDbAsync(async db =>
        {
            change(db);
            return await db.SaveChangesAsync();
        });

    /// <summary>Counted on a connection of its own: what another session sees right now.</summary>
    private int CountTasks(Guid taskId)
    {
        using var connection = new NpgsqlConnection(factory.DatabaseConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT count(*) FROM \"ProcessingTasks\" WHERE \"TaskId\" = @id", connection);
        command.Parameters.AddWithValue("id", taskId);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
