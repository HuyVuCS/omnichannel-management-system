using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Omnichannel.Workers.OrderWorker;
using Omnichannel.Workers.Processing;

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
        { Postgres(PostgresErrorCodes.UniqueViolation), ProcessingErrorClassifier.Unexpected },
        { Postgres(PostgresErrorCodes.CheckViolation), ProcessingErrorClassifier.CheckViolation },
        { Postgres(PostgresErrorCodes.NotNullViolation), ProcessingErrorClassifier.NotNullViolation },
        { Postgres(PostgresErrorCodes.ForeignKeyViolation), ProcessingErrorClassifier.ForeignKeyViolation },
        { Postgres(PostgresErrorCodes.NumericValueOutOfRange), ProcessingErrorClassifier.DataError },
        { Postgres(PostgresErrorCodes.InvalidTextRepresentation), ProcessingErrorClassifier.DataError },
        { new DbUpdateException("wrapped", Postgres(PostgresErrorCodes.CheckViolation)), ProcessingErrorClassifier.CheckViolation }
    };

    [Theory]
    [InlineData(ProcessingErrorClassifier.Deadlock, false)]
    [InlineData(ProcessingErrorClassifier.SerializationFailure, false)]
    [InlineData(ProcessingErrorClassifier.DatabaseUnavailable, false)]
    [InlineData(ProcessingErrorClassifier.Timeout, false)]
    [InlineData(ProcessingErrorClassifier.Unexpected, false)]
    [InlineData(ProcessingErrorClassifier.DataError, true)]
    [InlineData(ProcessingErrorClassifier.CheckViolation, true)]
    [InlineData(ProcessingErrorClassifier.NotNullViolation, true)]
    [InlineData(ProcessingErrorClassifier.ForeignKeyViolation, true)]
    public void Only_bad_input_or_state_is_permanent(string errorCode, bool permanent)
    {
        var exception = Cases().Select(row => (Exception)row[0]).First(e => ProcessingErrorClassifier.Classify(e).ErrorCode == errorCode);

        Assert.Equal(permanent, ProcessingErrorClassifier.Classify(exception).IsPermanent);
    }

    [Fact]
    public void Wrapped_database_errors_keep_the_postgres_message()
    {
        var error = ProcessingErrorClassifier.Classify(new DbUpdateException("An error occurred", Postgres(PostgresErrorCodes.CheckViolation)));

        Assert.Equal($"PostgreSQL {PostgresErrorCodes.CheckViolation}: test message", error.ErrorMessage);
    }

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

        var (_, message, _) = ProcessingErrorClassifier.Classify(exception);

        Assert.DoesNotContain("s3cr3t", message);
        Assert.Contains("Password=***", message);
        Assert.True(message.Length <= 500);
    }

    [Fact]
    public void Postgres_message_shows_sql_state_and_text_only()
    {
        var (_, message, _) = ProcessingErrorClassifier.Classify(Postgres(PostgresErrorCodes.DeadlockDetected));

        Assert.Equal($"PostgreSQL {PostgresErrorCodes.DeadlockDetected}: test message", message);
    }

    private static PostgresException Postgres(string sqlState) =>
        new("test message", "ERROR", "ERROR", sqlState);
}
