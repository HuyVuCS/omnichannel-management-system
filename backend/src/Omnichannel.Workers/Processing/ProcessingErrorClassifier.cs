using System.Net.Sockets;
using System.Text.RegularExpressions;
using Npgsql;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Processing;

namespace Omnichannel.Workers.Processing;

/// <summary>A classified failure of an attempt.</summary>
/// <param name="IsPermanent">True when retrying cannot help (bad input or state): final failure without retry.</param>
public sealed record ProcessingError(string ErrorCode, string ErrorMessage, bool IsPermanent);

/// <summary>
/// Maps an exception of a failed attempt to a stable <c>ErrorCode</c>, a sanitised <c>ErrorMessage</c> (dashboard-safe: no stack
/// trace, no connection string, no password) and the retry decision:
/// <list type="bullet">
/// <item>Transient → retry: deadlock, serialization failure, database unavailable, timeouts, the object storage not
/// answering or refusing (<c>storage_unavailable</c>), and <c>unexpected</c> (unknown exceptions are retried, but only up
/// to the attempt limit).</item>
/// <item>Permanent → no retry: PostgreSQL class 22 (data exception), 23514 (check), 23502 (not null), 23503 (foreign key).
/// 23505 (unique) is handled explicitly where it is expected and otherwise treated as unexpected.</item>
/// <item>A channel call (<see cref="ChannelApiException"/>) keeps its own code and message; it says itself whether trying again
/// can help: timeout, unavailable, rate limited and open circuit → retry; rejected by the channel → permanent.</item>
/// </list>
/// Shared by every worker, so all task types follow the same retry/DLQ model.
/// </summary>
public static partial class ProcessingErrorClassifier
{
    public const string Deadlock = "deadlock";
    public const string SerializationFailure = "serialization_failure";
    public const string DatabaseUnavailable = "db_unavailable";
    public const string Timeout = "timeout";
    public const string Unexpected = AttemptErrorCodes.Unexpected;

    /// <summary>The object storage did not answer or refused; its exception names the operation and nothing of the storage.</summary>
    public const string StorageUnavailable = ObjectStorageException.ErrorCode;

    public const string DataError = "data_error";
    public const string CheckViolation = "check_violation";
    public const string NotNullViolation = "not_null_violation";
    public const string ForeignKeyViolation = "foreign_key_violation";

    private static readonly HashSet<string> PermanentCodes = [DataError, CheckViolation, NotNullViolation, ForeignKeyViolation];

    private const int MaxMessageLength = 500;

    public static ProcessingError Classify(Exception exception)
    {
        if (FindInChain<ChannelApiException>(exception) is { } channel)
        {
            return new ProcessingError(channel.ErrorCode, Sanitize(channel.Message), IsPermanent: !channel.IsTransient);
        }

        var code = CodeOf(exception);
        var message = FindInChain<PostgresException>(exception) is { } postgres
            ? $"PostgreSQL {postgres.SqlState}: {postgres.MessageText}"
            : $"{exception.GetType().Name}: {exception.Message}";

        return new ProcessingError(code, Sanitize(message), PermanentCodes.Contains(code));
    }

    private static string CodeOf(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected } => Deadlock,
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } => SerializationFailure,
        // Class 08 (connection exception), 57P01-03 (admin shutdown, crash shutdown, cannot connect now), 53300 (too many connections).
        PostgresException postgres when postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
            || postgres.SqlState is PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown
                or PostgresErrorCodes.CannotConnectNow or PostgresErrorCodes.TooManyConnections => DatabaseUnavailable,
        PostgresException { SqlState: PostgresErrorCodes.CheckViolation } => CheckViolation,
        PostgresException { SqlState: PostgresErrorCodes.NotNullViolation } => NotNullViolation,
        PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } => ForeignKeyViolation,
        PostgresException postgres when postgres.SqlState.StartsWith("22", StringComparison.Ordinal) => DataError,
        ObjectStorageException => StorageUnavailable,
        TimeoutException => Timeout,
        NpgsqlException { InnerException: TimeoutException } => Timeout,
        NpgsqlException { InnerException: SocketException or IOException } => DatabaseUnavailable,
        NpgsqlException { IsTransient: true } => DatabaseUnavailable,
        // Wrappers (e.g. DbUpdateException, InvalidOperationException from EF) carry the database error inside.
        _ when exception.InnerException is { } inner => CodeOf(inner),
        _ => Unexpected
    };

    private static TException? FindInChain<TException>(Exception? exception)
        where TException : Exception
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is TException found)
            {
                return found;
            }
        }

        return null;
    }

    private static string Sanitize(string message)
    {
        // Defensive: never persist credentials even if a driver put a connection string into a message.
        var sanitized = SecretPattern().Replace(message, "$1=***");
        return sanitized.Length <= MaxMessageLength ? sanitized : sanitized[..MaxMessageLength];
    }

    [GeneratedRegex(@"(?i)\b(password|pwd)\s*=\s*[^;\s]*")]
    private static partial Regex SecretPattern();
}
