using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Omnichannel.Infrastructure.Persistence;

public static class DbUpdateExceptionExtensions
{
    /// <summary>
    /// True when the insert/update failed on the unique index or constraint <paramref name="constraintName"/>.
    /// The database is the uniqueness guard; services translate this into a 409 instead of check-then-insert.
    /// </summary>
    /// <param name="constraintName">
    /// A constant of <see cref="ExpectedUniqueViolations"/>. Any other name never matches: a violation that a service handles
    /// must be registered, otherwise it would still be logged as a database error.
    /// </param>
    public static bool IsUniqueViolation(this DbUpdateException exception, string constraintName) =>
        ExpectedUniqueViolations.ConstraintNames.Contains(constraintName)
        && exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        } postgres
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
