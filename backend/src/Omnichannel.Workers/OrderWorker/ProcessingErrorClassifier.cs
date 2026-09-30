using System.Net.Sockets;
using System.Text.RegularExpressions;
using Npgsql;

namespace Omnichannel.Workers.OrderWorker;

/// <summary>
/// Maps an exception of a failed attempt to a stable <c>ErrorCode</c> and a sanitised <c>ErrorMessage</c> (dashboard-safe:
/// no stack trace, no connection string, no password).
/// </summary>
public static partial class ProcessingErrorClassifier
{
    public const string Deadlock = "deadlock";
    public const string SerializationFailure = "serialization_failure";
    public const string DatabaseUnavailable = "db_unavailable";
    public const string Timeout = "timeout";
    public const string Unexpected = "unexpected";

    private const int MaxMessageLength = 500;

    public static (string ErrorCode, string ErrorMessage) Classify(Exception exception)
    {
        var code = CodeOf(exception);
        var message = exception is PostgresException postgres
            ? $"PostgreSQL {postgres.SqlState}: {postgres.MessageText}"
            : $"{exception.GetType().Name}: {exception.Message}";

        return (code, Sanitize(message));
    }

    private static string CodeOf(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected } => Deadlock,
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } => SerializationFailure,
        // Class 08 (connection exception), 57P01-03 (admin shutdown, crash shutdown, cannot connect now), 53300 (too many connections).
        PostgresException postgres when postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
            || postgres.SqlState is PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown
                or PostgresErrorCodes.CannotConnectNow or PostgresErrorCodes.TooManyConnections => DatabaseUnavailable,
        TimeoutException => Timeout,
        NpgsqlException { InnerException: TimeoutException } => Timeout,
        NpgsqlException { InnerException: SocketException or IOException } => DatabaseUnavailable,
        NpgsqlException { IsTransient: true } => DatabaseUnavailable,
        // Wrappers (e.g. DbUpdateException, InvalidOperationException from EF) carry the database error inside.
        _ when exception.InnerException is { } inner => CodeOf(inner),
        _ => Unexpected
    };

    private static string Sanitize(string message)
    {
        // Defensive: never persist credentials even if a driver put a connection string into a message.
        var sanitized = SecretPattern().Replace(message, "$1=***");
        return sanitized.Length <= MaxMessageLength ? sanitized : sanitized[..MaxMessageLength];
    }

    [GeneratedRegex(@"(?i)\b(password|pwd)\s*=\s*[^;\s]*")]
    private static partial Regex SecretPattern();
}
