using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Auth;

namespace Omnichannel.Tests.Auth;

/// <summary>Task 009: which already validated tokens are still rejected (security stamp, lockout, missing user).</summary>
public sealed class TokenRevocationTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task Token_with_the_current_stamp_of_an_unlocked_user_is_accepted()
    {
        var states = new StubStates(new UserSecurityState("stamp-1", null));

        var context = await ValidateAsync(states, UserId.ToString(), "stamp-1");

        Assert.Null(context.Result);
        Assert.Equal(UserId, Assert.Single(states.Lookups));
    }

    [Fact]
    public async Task Expired_lockout_does_not_reject()
    {
        var states = new StubStates(new UserSecurityState("stamp-1", DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Null((await ValidateAsync(states, UserId.ToString(), "stamp-1")).Result);
    }

    [Fact]
    public async Task Changed_stamp_rejects()
    {
        var states = new StubStates(new UserSecurityState("stamp-2", null));

        AssertRejected(await ValidateAsync(states, UserId.ToString(), "stamp-1"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60 * 24 * 365)]
    public async Task Locked_out_user_rejects(int minutesLeft)
    {
        var states = new StubStates(new UserSecurityState("stamp-1", DateTimeOffset.UtcNow.AddMinutes(minutesLeft)));

        AssertRejected(await ValidateAsync(states, UserId.ToString(), "stamp-1"));
    }

    [Fact]
    public async Task Unknown_user_rejects()
    {
        AssertRejected(await ValidateAsync(new StubStates(null), UserId.ToString(), "stamp-1"));
    }

    [Fact]
    public async Task User_without_a_stored_stamp_rejects()
    {
        AssertRejected(await ValidateAsync(new StubStates(new UserSecurityState(null, null)), UserId.ToString(), "stamp-1"));
    }

    [Theory]
    [InlineData(null, "stamp-1")]          // no subject
    [InlineData("not-a-guid", "stamp-1")]
    [InlineData("0199a1b0-0000-7000-8000-000000000001", null)] // token issued before the stamp claim existed
    [InlineData("0199a1b0-0000-7000-8000-000000000001", "")]
    public async Task Token_without_subject_or_stamp_rejects_without_a_lookup(string? subject, string? stamp)
    {
        var states = new StubStates(new UserSecurityState("stamp-1", null));

        AssertRejected(await ValidateAsync(states, subject, stamp));
        Assert.Empty(states.Lookups);
    }

    [Fact]
    public void Lockout_end_counts_until_it_has_passed()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.False(new UserSecurityState("s", null).IsLockedOut(now));
        Assert.False(new UserSecurityState("s", now.AddSeconds(-1)).IsLockedOut(now));
        Assert.True(new UserSecurityState("s", now).IsLockedOut(now));
        Assert.True(new UserSecurityState("s", DateTimeOffset.MaxValue).IsLockedOut(now));
    }

    private static void AssertRejected(TokenValidatedContext context)
    {
        Assert.NotNull(context.Result);
        Assert.NotNull(context.Result.Failure);
    }

    private static async Task<TokenValidatedContext> ValidateAsync(StubStates states, string? subject, string? stamp)
    {
        var claims = new List<Claim>();
        if (subject is not null)
        {
            claims.Add(new Claim(JwtClaimNames.Subject, subject));
        }

        if (stamp is not null)
        {
            claims.Add(new Claim(JwtClaimNames.SecurityStamp, stamp));
        }

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IUserSecurityStateProvider>(states)
                .AddSingleton(TimeProvider.System)
                .BuildServiceProvider()
        };
        var context = new TokenValidatedContext(
            httpContext,
            new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)),
            new JwtBearerOptions())
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
        };

        await TokenRevocation.ValidateAsync(context);
        return context;
    }

    private sealed class StubStates(UserSecurityState? state) : IUserSecurityStateProvider
    {
        public List<Guid> Lookups { get; } = [];

        public Task<UserSecurityState?> GetAsync(Guid userId, CancellationToken cancellationToken)
        {
            Lookups.Add(userId);
            return Task.FromResult(state);
        }

        public void Evict(Guid userId)
        {
        }
    }
}
