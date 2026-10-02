using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Api.Hubs;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Products;
using Omnichannel.Application.Realtime;
using Omnichannel.Application.Users;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 015: the hub with a real SignalR client against the real host - authentication (token in the query string for the
/// hub only), hints from a committed change, batching, revocation, nothing of the token in the logs, and the listener's
/// reconnect with <c>resync</c>.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RealtimeHubTests(ApiFactory factory)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Negotiate_needs_a_token_and_accepts_it_in_the_query_string_for_the_hub_only()
    {
        var anonymous = factory.CreateClient();
        var token = factory.IssueTokenFor(AppRoles.Staff);
        var negotiate = $"{NotificationsHub.Path}/negotiate?negotiateVersion=1";

        var withoutToken = await anonymous.PostAsync(negotiate, null);
        var withQueryToken = await anonymous.PostAsync($"{negotiate}&access_token={token}", null);
        var withHeader = await factory.CreateClientAs(AppRoles.Staff).PostAsync(negotiate, null);
        var withBrokenToken = await anonymous.PostAsync($"{negotiate}&access_token={token[..^4]}AAAA", null);
        // Every other path reads the header only.
        var otherPath = await anonymous.GetAsync($"/api/products?access_token={token}");

        Assert.Equal(HttpStatusCode.Unauthorized, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withQueryToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withHeader.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withBrokenToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherPath.StatusCode);
    }

    [Fact]
    public async Task Staff_client_receives_a_hint_after_a_stock_import()
    {
        var manager = factory.CreateClientAs(AppRoles.Manager);
        var product = await (await manager.PostAsJsonAsync(
                "/api/products", new CreateProductRequest($"HUB-{TestData.UniqueSuffix()}".ToUpperInvariant(), "Hub product", 1m, 0, null)))
            .ReadAsync<ProductResponse>();
        await using var client = await ConnectAsync(factory.IssueTokenFor(AppRoles.Staff));

        var import = await manager.PostAsJsonAsync(
            "/api/inventory/import",
            new ImportInventoryRequest($"GRN-{TestData.UniqueSuffix()}", null, [new ImportInventoryItem(product.ProductId, 5)]));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        var message = await client.WaitForAsync(m => m.Events.Any(e => e.ProductIds?.Contains(product.ProductId) == true), "inventory.changed for the product");
        var hint = message.Events.Single(e => e.ProductIds?.Contains(product.ProductId) == true);
        Assert.Equal(RealtimeEventTypes.InventoryChanged, hint.Type);
        Assert.False(message.Resync);
        // A hint, not data: no stock value, nothing but the id.
        Assert.Null(hint.Status);
        Assert.Null(hint.TaskId);
        Assert.Null(hint.OrderId);
        Assert.Null(hint.All);
    }

    [Fact]
    public async Task Two_units_of_work_within_the_window_arrive_as_one_message_in_order()
    {
        await using var client = await ConnectAsync(factory.IssueTokenFor(AppRoles.Staff));
        var first = new ProcessingTask(ProcessingTaskType.Invoice, null, $"hub-{TestData.UniqueSuffix()}", null);
        var second = new ProcessingTask(ProcessingTaskType.Email, null, first.CorrelationId, null);

        // Two saves, two notifications, a few milliseconds apart (the window is 250 ms).
        await factory.QueryDbAsync(async db =>
        {
            db.ProcessingTasks.Add(first);
            await db.SaveChangesAsync();
            db.ProcessingTasks.Add(second);
            return await db.SaveChangesAsync();
        });

        var message = await client.WaitForAsync(m => m.Events.Any(e => e.TaskId == second.TaskId), "hint for the second task");
        Assert.Equal(
            [first.TaskId, second.TaskId],
            message.Events.Where(e => e.TaskId == first.TaskId || e.TaskId == second.TaskId).Select(e => e.TaskId!.Value));
        Assert.Equal(1, client.Messages.Count(m => m.Events.Any(e => e.TaskId == first.TaskId || e.TaskId == second.TaskId)));
        var hint = message.Events.Single(e => e.TaskId == second.TaskId);
        Assert.Equal((RealtimeEventTypes.ProcessingTaskChanged, "Email", "Pending"), (hint.Type, hint.TaskType, hint.Status));
    }

    [Fact]
    public async Task User_whose_security_stamp_changed_cannot_connect()
    {
        var admin = factory.CreateClientAs(AppRoles.Admin);
        var email = $"hub-{TestData.UniqueSuffix()}@integration.test";
        var password = $"Hub-{Guid.NewGuid():N}-Aa1";
        var user = await (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, password, AppRoles.Staff))).ReadAsync<UserResponse>();
        var login = await (await factory.CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password)))
            .ReadAsync<LoginResult>();

        // Valid token: connects.
        await using (await ConnectAsync(login.AccessToken))
        {
        }

        // A role change issues a new security stamp: the old token is refused at the next connect.
        var changed = await admin.PutAsJsonAsync($"/api/users/{user.UserId}/role", new ChangeUserRoleRequest(AppRoles.Manager));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(login.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Access_token_of_the_query_string_appears_in_no_log_event()
    {
        var startedAt = DateTimeOffset.Now;
        var token = factory.IssueTokenFor(AppRoles.Staff);
        var task = new ProcessingTask(ProcessingTaskType.Invoice, null, $"hub-{TestData.UniqueSuffix()}", null);

        await using (var client = await ConnectAsync(token))
        {
            await factory.QueryDbAsync(async db =>
            {
                db.ProcessingTasks.Add(task);
                return await db.SaveChangesAsync();
            });
            await client.WaitForAsync(m => m.Events.Any(e => e.TaskId == task.TaskId), "hint for the task");
        }

        var logged = factory.Logs.RenderedSince(startedAt);
        Assert.DoesNotContain(logged, entry => entry.Contains(token, StringComparison.Ordinal));
        // The signature alone would be enough to worry about: not there either.
        Assert.DoesNotContain(logged, entry => entry.Contains(token[(token.LastIndexOf('.') + 1)..], StringComparison.Ordinal));

        // The sources that see the URL were active (captured at Information / Debug in this host) and show the redaction.
        Assert.Contains(logged, entry => entry.Contains("access_token=***", StringComparison.Ordinal)
            && entry.Contains(NotificationsHub.Path, StringComparison.Ordinal)
            && entry.Contains("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal));
        Assert.Contains(logged, entry => entry.Contains("Microsoft.AspNetCore.Http.Connections", StringComparison.Ordinal));
        Assert.Contains(logged, entry => entry.Contains("Microsoft.AspNetCore.SignalR", StringComparison.Ordinal));
        // The listener's own entries at Debug carry event types only.
        Assert.Contains(logged, entry => entry.Contains("Hint batch sent to the clients", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Listener_reconnects_after_losing_its_connection_and_tells_clients_to_resync()
    {
        var listener = factory.Services.GetRequiredService<PostgresHintListener>();
        await Eventually.WaitAsync(() => Task.FromResult(listener.IsListening), listening => listening, Timeout, "listener connected");
        await using var client = await ConnectAsync(factory.IssueTokenFor(AppRoles.Staff));
        var reconnectsBefore = listener.Reconnects;

        // The database ends the listener's session (as a failover or a restart would).
        var terminated = await factory.QueryDbAsync(db => db.Database
            .SqlQuery<bool>($"SELECT pg_terminate_backend(pid) AS \"Value\" FROM pg_stat_activity WHERE application_name = {PostgresHintListener.ApplicationName}")
            .ToListAsync());
        Assert.Equal([true], terminated);

        // Hints sent while it was away are lost, so every client is told to refetch.
        var resync = await client.WaitForAsync(m => m.Resync, "resync after the reconnect");
        Assert.Empty(resync.Events);
        Assert.Equal(reconnectsBefore + 1, listener.Reconnects);
        Assert.True(listener.IsListening);

        // And hints arrive again.
        var task = new ProcessingTask(ProcessingTaskType.Invoice, null, $"hub-{TestData.UniqueSuffix()}", null);
        await factory.QueryDbAsync(async db =>
        {
            db.ProcessingTasks.Add(task);
            return await db.SaveChangesAsync();
        });
        var after = await client.WaitForAsync(m => m.Events.Any(e => e.TaskId == task.TaskId), "hint after the reconnect");
        Assert.False(after.Resync);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A real client with the token in the query string (what a browser does). Long polling: the test server has no sockets;
    /// authentication and the hub pipeline are the same for every transport.
    /// </summary>
    private async Task<HubClient> ConnectAsync(string accessToken)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress, $"{NotificationsHub.Path}?access_token={accessToken}"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                })
            .Build();
        var client = new HubClient(connection);
        try
        {
            await connection.StartAsync();
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return client;
    }

    private sealed class HubClient : IAsyncDisposable
    {
        private readonly HubConnection _connection;
        private readonly ConcurrentQueue<RealtimeHintMessage> _messages = new();

        public HubClient(HubConnection connection)
        {
            _connection = connection;
            connection.On<RealtimeHintMessage>(RealtimeChannel.HubMethod, message => _messages.Enqueue(message));
        }

        public IReadOnlyList<RealtimeHintMessage> Messages => _messages.ToArray();

        public async Task<RealtimeHintMessage> WaitForAsync(Func<RealtimeHintMessage, bool> match, string description) =>
            (await Eventually.WaitAsync(() => Task.FromResult(Messages), all => all.Any(match), Timeout, description)).First(match);

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
