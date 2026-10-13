using Microsoft.EntityFrameworkCore;

namespace Omnichannel.Infrastructure.Persistence;

/// <summary>
/// E-mail addresses of the users a page of rows names in an actor column (<c>CreatedBy</c>, <c>PaidBy</c>,
/// <c>AcknowledgedBy</c>): text columns that hold a user id or another actor (<c>system</c>, <c>system:{worker}</c>,
/// <c>webhook:{slug}</c>). One statement for the whole page, by primary key - a comparison of <c>Id::text</c> per row
/// cannot use it.
/// </summary>
internal static class UserEmailLookup
{
    /// <summary>
    /// The e-mail address of every actor of <paramref name="actors"/> that is the id of an existing user, by the actor
    /// text. Other actors and users that are gone are not in the result. No statement runs when no actor is a user id.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string?>> UserEmailsAsync(
        this OmnichannelDbContext dbContext,
        IEnumerable<string?> actors,
        CancellationToken cancellationToken)
    {
        var userIds = actors.Select(UserIdOf).OfType<Guid>().Distinct().ToList();
        if (userIds.Count == 0)
        {
            return new Dictionary<string, string?>();
        }

        var users = await dbContext.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email })
            .ToListAsync(cancellationToken);
        return users.ToDictionary(u => u.Id.ToString(), u => u.Email, StringComparer.Ordinal);
    }

    public static string? EmailOf(this IReadOnlyDictionary<string, string?> emails, string? actor) =>
        actor is null ? null : emails.GetValueOrDefault(actor);

    // Only the text PostgreSQL writes for a uuid (and the application stores for a user) names a user - exactly what
    // the comparison with Id::text matched.
    private static Guid? UserIdOf(string? actor) =>
        Guid.TryParseExact(actor, "D", out var id) && string.Equals(id.ToString(), actor, StringComparison.Ordinal) ? id : null;
}
