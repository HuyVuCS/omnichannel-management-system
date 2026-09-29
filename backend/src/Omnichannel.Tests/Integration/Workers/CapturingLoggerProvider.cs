using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>Collects formatted log messages of a test host so tests can assert on worker behaviour.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries => _entries.ToArray();

    public int Count(string category, LogLevel level, string messageFragment) =>
        _entries.Count(e => e.Category == category && e.Level == level && e.Message.Contains(messageFragment, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
