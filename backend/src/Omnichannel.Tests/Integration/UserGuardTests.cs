using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Users;
using Omnichannel.Infrastructure;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 009, <c>last_admin</c> guard. The shared API test database always contains other admins (the factory's own and those
/// created by other tests), so "the last admin" cannot be constructed there. These tests call <see cref="IUserService"/>
/// with the production registrations against a database of their own (created per test on the same PostgreSQL server),
/// where the set of admins is fully controlled. The actor is set per scope, as the JWT subject is in the API.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UserGuardTests(ApiFactory factory) : IAsyncLifetime
{
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(factory.DatabaseConnectionString)
        {
            Database = $"user_guards_{Guid.NewGuid():N}"
        }.ConnectionString;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString })
            .Build());
        services.AddInfrastructure();
        services.AddIdentityInfrastructure();
        services.AddScoped<TestActor>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<TestActor>());
        services.AddScoped<ICorrelationContext, TestCorrelation>();
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>().Database.MigrateAsync(); // creates the database
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task The_last_unlocked_admin_cannot_be_demoted_or_locked()
    {
        var admin = await CreateAsync(AppRoles.Admin);
        var staff = await CreateAsync(AppRoles.Staff);

        // Possible in practice with a token that is still accepted although its owner is no admin any more (cache window).
        var demote = await Assert.ThrowsAsync<ConflictException>(() => AsAsync(staff, users =>
            users.ChangeRoleAsync(admin, new ChangeUserRoleRequest(AppRoles.Manager), CancellationToken.None)));
        Assert.Equal(UserErrorCodes.LastAdmin, demote.ErrorCode);

        var locked = await Assert.ThrowsAsync<ConflictException>(() => AsAsync(staff, users => users.LockAsync(admin, CancellationToken.None)));
        Assert.Equal(UserErrorCodes.LastAdmin, locked.ErrorCode);

        var unchanged = await AsAsync(staff, users => users.GetAsync(admin, CancellationToken.None));
        Assert.Equal((AppRoles.Admin, false), (unchanged.Role, unchanged.IsLocked));
    }

    [Fact]
    public async Task A_locked_admin_does_not_count_as_a_remaining_admin()
    {
        var first = await CreateAsync(AppRoles.Admin);
        var second = await CreateAsync(AppRoles.Admin);

        // With two admins either may be locked ...
        Assert.True((await AsAsync(first, users => users.LockAsync(second, CancellationToken.None))).IsLocked);

        // ... but then the other one is the last unlocked admin: a third party can neither demote nor lock them.
        var staff = await CreateAsync(AppRoles.Staff);
        var demote = await Assert.ThrowsAsync<ConflictException>(() => AsAsync(staff, users =>
            users.ChangeRoleAsync(first, new ChangeUserRoleRequest(AppRoles.Staff), CancellationToken.None)));
        Assert.Equal(UserErrorCodes.LastAdmin, demote.ErrorCode);

        // Demoting the locked one changes nothing about the unlocked admins: allowed.
        Assert.Equal(AppRoles.Manager,
            (await AsAsync(first, users => users.ChangeRoleAsync(second, new ChangeUserRoleRequest(AppRoles.Manager), CancellationToken.None))).Role);

        // After promoting someone else the first admin can be demoted.
        await AsAsync(first, users => users.ChangeRoleAsync(staff, new ChangeUserRoleRequest(AppRoles.Admin), CancellationToken.None));
        Assert.Equal(AppRoles.Staff,
            (await AsAsync(staff, users => users.ChangeRoleAsync(first, new ChangeUserRoleRequest(AppRoles.Staff), CancellationToken.None))).Role);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_admins_removing_each_other_concurrently_leave_exactly_one(bool byLocking)
    {
        for (var round = 0; round < 5; round++)
        {
            var a = await CreateAsync(AppRoles.Admin);
            var b = await CreateAsync(AppRoles.Admin);

            Task<UserResponse> Remove(Guid actor, Guid target) => AsAsync(actor, users => byLocking
                ? users.LockAsync(target, CancellationToken.None)
                : users.ChangeRoleAsync(target, new ChangeUserRoleRequest(AppRoles.Staff), CancellationToken.None));

            var outcomes = await Task.WhenAll(Outcome(Remove(a, b)), Outcome(Remove(b, a)));

            Assert.Equal(1, outcomes.Count(o => o is null));
            Assert.Equal(UserErrorCodes.LastAdmin, Assert.Single(outcomes, o => o is not null));
            Assert.Equal(1, await UnlockedAdminsAsync());

            // The next round starts with a new pair as the only two admins.
            await RemoveAllAdminsAsync();
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static async Task<string?> Outcome(Task<UserResponse> operation)
    {
        try
        {
            await operation;
            return null;
        }
        catch (ConflictException conflict)
        {
            return conflict.ErrorCode;
        }
    }

    private Task<Guid> CreateAsync(string role) => AsAsync(Guid.NewGuid(), async users =>
        (await users.CreateAsync(
            new CreateUserRequest($"guard.{Guid.NewGuid():N}@guards.test", $"Passw0rd-{Guid.NewGuid():N}", role),
            CancellationToken.None)).UserId);

    /// <summary>Runs <paramref name="action"/> in its own scope with <paramref name="actor"/> as the current user.</summary>
    private async Task<T> AsAsync<T>(Guid actor, Func<IUserService, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestActor>().UserId = actor.ToString();
        return await action(scope.ServiceProvider.GetRequiredService<IUserService>());
    }

    private async Task<int> UnlockedAdminsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var page = await scope.ServiceProvider.GetRequiredService<IUserService>()
            .ListAsync(new UserListQuery { Role = AppRoles.Admin, Locked = false }, CancellationToken.None);
        return page.TotalCount;
    }

    /// <summary>Test clean-up only (bypasses the guard on purpose): no admin is left for the next round.</summary>
    private async Task RemoveAllAdminsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>();
        var adminRoleId = await db.Roles.Where(r => r.Name == AppRoles.Admin).Select(r => r.Id).SingleAsync();
        await db.UserRoles.Where(ur => ur.RoleId == adminRoleId).ExecuteDeleteAsync();
    }

    private sealed class TestActor : ICurrentUser
    {
        public string? UserId { get; set; }
    }

    private sealed class TestCorrelation : ICorrelationContext
    {
        public string CorrelationId => "user-guard-tests";
    }
}
