using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Auth;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Infrastructure.Identity;

/// <summary>
/// One small query per user and cache window (<see cref="TokenRevocationOptions.CacheDuration"/>), cached in-process only.
/// "User not found" is cached as well.
/// </summary>
internal sealed class UserSecurityStateProvider(
    OmnichannelDbContext dbContext,
    IMemoryCache cache,
    IOptions<TokenRevocationOptions> options) : IUserSecurityStateProvider
{
    public async Task<UserSecurityState?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var duration = options.Value.CacheDuration;
        if (duration > TimeSpan.Zero && cache.TryGetValue(KeyOf(userId), out Entry? cached))
        {
            return cached!.State;
        }

        var row = await dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp })
            .SingleOrDefaultAsync(cancellationToken);

        var state = row is null ? null : new UserSecurityState(row.SecurityStamp);
        if (duration > TimeSpan.Zero)
        {
            cache.Set(KeyOf(userId), new Entry(state), duration);
        }

        return state;
    }

    public void Evict(Guid userId) => cache.Remove(KeyOf(userId));

    private static string KeyOf(Guid userId) => $"user-security-state:{userId:N}";

    /// <summary>Wrapper so that "no such user" (null state) is a cache hit too.</summary>
    private sealed record Entry(UserSecurityState? State);
}
