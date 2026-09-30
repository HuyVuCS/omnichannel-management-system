using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Omnichannel.Workers.OrderWorker;

namespace Omnichannel.Tests.Workers;

public sealed class ProcessingErrorClassifierTests
{
    public static TheoryData<Exception, string> Cases() => new()
    {
        { Postgres(PostgresErrorCodes.DeadlockDetected), ProcessingErrorClassifier.Deadlock },
        { Postgres(PostgresErrorCodes.SerializationFailure), ProcessingErrorClassifier.SerializationFailure },
        { Postgres(PostgresErrorCodes.AdminShutdown), ProcessingErrorClassifier.DatabaseUnavailable },
        { Postgres("08006"), ProcessingErrorClassifier.DatabaseUnavailable },
        { new NpgsqlException("Exception while connecting", new SocketException(10061)), ProcessingErrorClassifier.DatabaseUnavailable },
        { new NpgsqlException("Exception while reading", new TimeoutException()), ProcessingErrorClassifier.Timeout },
        { new TimeoutException("slow"), ProcessingErrorClassifier.Timeout },
        { new DbUpdateException("wrapped", Postgres(PostgresErrorCodes.DeadlockDetected)), ProcessingErrorClassifier.Deadlock },
        { new InvalidOperationException("outer", new InvalidOperationException("inner")), ProcessingErrorClassifier.Unexpected },
        { Postgres(PostgresErrorCodes.UniqueViolation), ProcessingErrorClassifier.Unexpected }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Maps_exceptions_to_stable_error_codes(Exception exception, string expectedCode)
    {
        Assert.Equal(expectedCode, ProcessingErrorClassifier.Classify(exception).ErrorCode);
    }

    [Fact]
    public void Message_is_short_and_never_contains_a_password()
    {
        var exception = new InvalidOperationException(
            "Failed: Host=db;Username=app;Password=s3cr3t;Database=x " + new string('x', 2000));

        var (_, message) = ProcessingErrorClassifier.Classify(exception);

        Assert.DoesNotContain("s3cr3t", message);
        Assert.Contains("Password=***", message);
        Assert.True(message.Length <= 500);
    }

    [Fact]
    public void Postgres_message_shows_sql_state_and_text_only()
    {
        var (_, message) = ProcessingErrorClassifier.Classify(Postgres(PostgresErrorCodes.DeadlockDetected));

        Assert.Equal($"PostgreSQL {PostgresErrorCodes.DeadlockDetected}: test message", message);
    }

    private static PostgresException Postgres(string sqlState) =>
        new("test message", "ERROR", "ERROR", sqlState);
}
