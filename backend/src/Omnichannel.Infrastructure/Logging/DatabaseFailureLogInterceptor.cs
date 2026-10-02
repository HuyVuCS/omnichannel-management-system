using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Infrastructure.Logging;

/// <summary>
/// Decides the log level of a database failure by its cause (API and Workers, through <c>AddInfrastructure</c>).
/// <para>
/// EF Core writes two Error entries for a failed <c>SaveChanges</c>: <c>Database.Command</c> / <c>CommandError</c> (20102, the
/// SQL text, <b>no exception attached</b>) and <c>Update</c> / <c>SaveChangesFailed</c> (10000, with the exception) - also when
/// the failure is a unique violation that a service maps to a 409 or an idempotent outcome
/// (<see cref="ExpectedUniqueViolations"/>). Operators must not see such business conflicts as errors.
/// </para>
/// <para>
/// The level of an EF Core event is fixed per event id, and the first entry does not carry the exception, so a log filter
/// cannot tell an expected conflict from a real failure. Therefore both events are configured at Debug
/// (<see cref="Configure"/>) and this interceptor - which receives the exception for both - writes them again at Error, with
/// EF Core's category, event id and message, unless the cause is a registered unique violation. Every other database failure
/// (a unique violation on any other constraint, any other SQL state, a connection error) is logged at Error as before.
/// </para>
/// </summary>
public sealed class DatabaseFailureLogInterceptor(ILoggerFactory loggerFactory) : IDbCommandInterceptor, ISaveChangesInterceptor
{
    private readonly ILogger _commandLogger = loggerFactory.CreateLogger(DbLoggerCategory.Database.Command.Name);
    private readonly ILogger _updateLogger = loggerFactory.CreateLogger(DbLoggerCategory.Update.Name);

    /// <summary>Moves EF Core's own entries for the two failure events to Debug; this interceptor writes the Error entries.</summary>
    public static void Configure(DbContextOptionsBuilder options, DatabaseFailureLogInterceptor interceptor)
    {
        options.ConfigureWarnings(warnings => warnings.Log(
            (RelationalEventId.CommandError, LogLevel.Debug),
            (CoreEventId.SaveChangesFailed, LogLevel.Debug)));
        options.AddInterceptors(interceptor);
    }

    public void CommandFailed(DbCommand command, CommandErrorEventData eventData) => LogCommandError(eventData);

    public Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        LogCommandError(eventData);
        return Task.CompletedTask;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => LogSaveChangesFailed(eventData);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        LogSaveChangesFailed(eventData);
        return Task.CompletedTask;
    }

    private void LogCommandError(CommandErrorEventData eventData)
    {
        if (!ExpectedUniqueViolations.Contains(eventData.Exception))
        {
            // As EF Core writes it: the SQL text (parameter values only with sensitive-data logging), without the exception.
            _commandLogger.Log(LogLevel.Error, eventData.EventId, eventData.ToString(), exception: null, static (message, _) => message);
        }
    }

    private void LogSaveChangesFailed(DbContextErrorEventData eventData)
    {
        // The interceptor is also called for optimistic-concurrency failures (their own event, Debug in EF Core): unchanged.
        if (eventData.EventId.Id == CoreEventId.SaveChangesFailed.Id && !ExpectedUniqueViolations.Contains(eventData.Exception))
        {
            _updateLogger.Log(LogLevel.Error, eventData.EventId, eventData.ToString(), eventData.Exception, static (message, _) => message);
        }
    }
}
