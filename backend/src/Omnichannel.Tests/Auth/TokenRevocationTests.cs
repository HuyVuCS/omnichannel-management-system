using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Auth;

namespace Omnichannel.Tests.Auth;

/// <summary>
/// Which already validated tokens are still rejected: changed security stamp or missing user (task 009). Lockout is not a
/// reason (task 010, review 009 option A); that is covered end to end in <c>UserManagementTests</c>.
/// </summary>
public sealed class TokenRevocationTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task Token_with_the_current_stamp_of_an_unlocked_user_is_accepted()
    {
        var states = new StubStates(new UserSecurityState("stamp-1"));

        var context = await ValidateAsync(states, UserId.ToString(), "stamp-1");

        Assert.Null(context.Result);
        Assert.Equal(UserId, Assert.Single(states.Lookups));
    }

    [Fact]
    public async Task Changed_stamp_rejects()
    {
        var states = new StubStates(new UserSecurityState("stamp-2"));

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
        AssertRejected(await ValidateAsync(new StubStates(new UserSecurityState(null)), UserId.ToString(), "stamp-1"));
    }

    [Theory]
    [InlineData(null, "stamp-1")]          // no subject
    [InlineData("not-a-guid", "stamp-1")]
    [InlineData("0199a1b0-0000-7000-8000-000000000001", null)] // token issued before the stamp claim existed
    [InlineData("0199a1b0-0000-7000-8000-000000000001", "")]
    public async Task Token_without_subject_or_stamp_rejects_without_a_lookup(string? subject, string? stamp)
    {
        var states = new StubStates(new UserSecurityState("stamp-1"));

        AssertRejected(await ValidateAsync(states, subject, stamp));
        Assert.Empty(states.Lookups);
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
