using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Omnichannel.Infrastructure.Persistence;

public static class DbUpdateExceptionExtensions
{
    /// <summary>
    /// True when the insert/update failed on the unique index or constraint <paramref name="constraintName"/>.
    /// The database is the uniqueness guard; services translate this into a 409 instead of check-then-insert.
    /// </summary>
    public static bool IsUniqueViolation(this DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        } postgres
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
