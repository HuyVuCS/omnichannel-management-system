using System.Collections.Concurrent;
using System.Text;
using Npgsql;
using Omnichannel.Application.Realtime;

namespace Omnichannel.Tests.Integration;

/// <summary>One notification as PostgreSQL delivered it.</summary>
/// <param name="CheckResult">Result of the probe's check, evaluated at the moment of delivery (null without a check).</param>
public sealed record ReceivedNotification(string Payload, RealtimeHintBatch? Batch, bool? CheckResult)
{
    public int PayloadBytes => Encoding.UTF8.GetByteCount(Payload);

    public IReadOnlyList<RealtimeHint> Events => Batch?.Events ?? [];

    public bool HasTask(Guid taskId) => Events.Any(e => e.TaskId == taskId);
}

/// <summary>
/// A separate <c>LISTEN omnichannel_ui</c> connection for tests: what a listener of another process would receive, in the
/// order of delivery.
/// </summary>
public sealed class HintProbe : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<ReceivedNotification> _received = new();
    private Task _loop = Task.CompletedTask;

    private HintProbe(NpgsqlConnection connection) => _connection = connection;

    /// <summary>Evaluated when a notification arrives (e.g. "is the row visible to another connection right now?").</summary>
    public Func<bool>? Check { get; set; }

    public IReadOnlyList<ReceivedNotification> Notifications => _received.ToArray();

    public IEnumerable<RealtimeHint> Events => _received.SelectMany(n => n.Events);

    public static async Task<HintProbe> StartAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        var probe = new HintProbe(connection);
        connection.Notification += (_, e) =>
            probe._received.Enqueue(new ReceivedNotification(e.Payload, RealtimeHintBatch.TryParse(e.Payload), probe.Check?.Invoke()));
        await connection.OpenAsync();
        await using (var listen = new NpgsqlCommand($"LISTEN {RealtimeChannel.Name}", connection))
        {
            await listen.ExecuteNonQueryAsync();
        }

        probe._loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await connection.WaitAsync(probe._stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Probe disposed.
            }
        });
        return probe;
    }

    public Task<IReadOnlyList<ReceivedNotification>> WaitForAsync(
        Func<IReadOnlyList<ReceivedNotification>, bool> done, string description, TimeSpan? timeout = null) =>
        Eventually.WaitAsync(() => Task.FromResult(Notifications), done, timeout ?? TimeSpan.FromSeconds(30), description);

    public async Task<ReceivedNotification> WaitForTaskAsync(Guid taskId, string? status = null) =>
        (await WaitForAsync(
            all => all.Any(n => n.Events.Any(e => e.TaskId == taskId && (status is null || e.Status == status))),
            $"hint for task {taskId} {status}"))
        .First(n => n.Events.Any(e => e.TaskId == taskId && (status is null || e.Status == status)));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        await _connection.DisposeAsync();
        _stop.Dispose();
    }
}
