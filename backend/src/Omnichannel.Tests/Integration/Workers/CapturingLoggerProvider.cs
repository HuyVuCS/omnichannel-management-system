using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>A captured log entry with the key/value pairs of all active scopes.</summary>
public sealed record CapturedLog(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Scope,
    Exception? Exception = null);

/// <summary>Collects formatted log messages (and scope values) of a test host so tests can assert on worker behaviour.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();
    private readonly AsyncLocal<ImmutableStack<object>> _scopes = new();

    public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

    public int Count(string category, LogLevel level, string messageFragment) =>
        _entries.Count(e => e.Category == category && e.Level == level && e.Message.Contains(messageFragment, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void Dispose()
    {
    }

    private IReadOnlyDictionary<string, object?> CurrentScope()
    {
        var values = new Dictionary<string, object?>();
        foreach (var state in (_scopes.Value ?? ImmutableStack<object>.Empty).Reverse())
        {
            if (state is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    values[key] = value;
                }
            }
        }

        return values;
    }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            var previous = provider._scopes.Value ?? ImmutableStack<object>.Empty;
            provider._scopes.Value = previous.Push(state);
            return new ScopeHandle(() => provider._scopes.Value = previous);
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider._entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), provider.CurrentScope(), exception));
    }

    private sealed class ScopeHandle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
