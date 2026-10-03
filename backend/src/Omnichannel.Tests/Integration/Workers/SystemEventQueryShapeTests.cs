using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Processing;
using Omnichannel.Application.SystemEvents;
using Omnichannel.Application.Users;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Identity;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 018b Part B: the Logs/Audit list sends one COUNT, one page query and at most one user lookup, however many rows
/// of the page name users. Counts the SQL commands EF Core sends (runs in the Workers collection because only there a
/// host with an extra DbContext interceptor can be built, as <see cref="QueryShapeTests"/> does).
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SystemEventQueryShapeTests(WorkersFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task List_is_count_plus_page_plus_one_user_lookup_for_the_whole_page()
    {
        var correlationId = $"sysevt-shape-{Guid.NewGuid():N}";
        var users = await SeedAsync(correlationId);
        var commands = new CommandCounter();
        using var host = fixture.CreateHost(
            "system-event-shape",
            configureServices: services => services.ConfigureDbContext<OmnichannelDbContext>(o => o.AddInterceptors(commands)));
        await using var scope = host.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<ISystemEventService>();

        // Twelve rows naming three different users as actor or target: COUNT + page + one lookup.
        var withUsers = await commands.CountAsync(
            () => events.ListAsync(new SystemEventListQuery { CorrelationId = correlationId }, includeUserEvents: true, CancellationToken.None),
            expected: 3);
        Assert.Equal(12, withUsers.TotalCount);
        Assert.Equal(
            users.Select(u => u.Email!).Order(),
            withUsers.Items.SelectMany(i => new[] { i.ActorEmail, i.TargetEmail }).OfType<string>().Distinct().Order());
        Assert.All(withUsers.Items.Where(i => i.EventType == UserEvents.Locked), i => Assert.NotNull(i.TargetEmail));

        // A page that names nobody: no lookup at all.
        var withoutUsers = await commands.CountAsync(
            () => events.ListAsync(
                new SystemEventListQuery { CorrelationId = correlationId, EventType = [ProcessingEvents.Failed] },
                includeUserEvents: true,
                CancellationToken.None),
            expected: 2);
        Assert.Equal(4, withoutUsers.TotalCount);
        Assert.All(withoutUsers.Items, i => Assert.Equal(((string?)null, (string?)null), (i.ActorEmail, i.TargetEmail)));

        // The role rule is part of the same two statements.
        var forManager = await commands.CountAsync(
            () => events.ListAsync(new SystemEventListQuery { CorrelationId = correlationId }, includeUserEvents: false, CancellationToken.None),
            expected: 3);
        Assert.Equal(8, forManager.TotalCount);
        Assert.DoesNotContain(forManager.Items, i => i.EventType.StartsWith(SystemEventVisibility.AdminOnlyPrefix, StringComparison.Ordinal));
    }

    /// <summary>Four user events, four manual retries (each names a user) and four failures (no user), in turn.</summary>
    private async Task<List<ApplicationUser>> SeedAsync(string correlationId) =>
        await fixture.WithDbAsync(async db =>
        {
            var users = Enumerable.Range(0, 3)
                .Select(n =>
                {
                    var email = $"shape-{n}-{Guid.NewGuid():N}@integration.test";
                    return new ApplicationUser
                    {
                        UserName = email,
                        NormalizedUserName = email.ToUpperInvariant(),
                        Email = email,
                        NormalizedEmail = email.ToUpperInvariant(),
                        SecurityStamp = Guid.NewGuid().ToString("N")
                    };
                })
                .ToList();
            db.Users.AddRange(users);

            for (var n = 0; n < 4; n++)
            {
                var actor = users[n % 3].Id;
                var target = users[(n + 1) % 3].Id;
                db.SystemEventLogs.AddRange(
                    Event(UserEvents.Locked, correlationId, n * 3, new { targetUserId = target, actorUserId = actor }),
                    Event(ProcessingEvents.RetryRequested, correlationId, n * 3 + 1, new { taskId = Guid.NewGuid(), requestedBy = actor }),
                    Event(ProcessingEvents.Failed, correlationId, n * 3 + 2, new { taskId = Guid.NewGuid(), errorCode = "timeout" }));
            }

            await db.SaveChangesAsync();
            return users;
        });

    private static SystemEventLog Event(string eventType, string correlationId, int second, object payload) =>
        new(SystemEventLevel.Information, eventType, correlationId, null, null, null, MessageJson.Serialize(payload), T0.AddSeconds(second));

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

        // Synchronous commands count as well: a lookup per row would most likely be written as one.
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Interlocked.Increment(ref _count);
            return base.ScalarExecuting(command, eventData, result);
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
