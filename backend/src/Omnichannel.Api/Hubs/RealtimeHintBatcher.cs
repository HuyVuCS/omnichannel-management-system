using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Realtime;

namespace Omnichannel.Api.Hubs;

/// <summary>
/// Collects the hints the listener receives and hands them on in batches: the first hint opens a window
/// (<see cref="RealtimeOptions.BatchWindow"/>); everything that arrives until it closes is sent as one message, in arrival
/// order. A batch that grew beyond <see cref="MaxEventsPerMessage"/>, or hints that could not be queued, become
/// <c>resync</c>: the clients refetch instead of receiving a list nobody can use.
/// </summary>
public sealed class RealtimeHintBatcher(IOptions<RealtimeOptions> options, TimeProvider timeProvider, ILogger<RealtimeHintBatcher> logger)
{
    public const int MaxEventsPerMessage = 200;
    private const int QueueCapacity = 10_000;

    private readonly Channel<Item> _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    private int _overflowed;

    public void Add(IReadOnlyList<RealtimeHint> events) => Write(new Item(events, Resync: false));

    /// <summary>Hints may have been missed (the listener was disconnected): the next message tells clients to refetch.</summary>
    public void RequestResync() => Write(new Item([], Resync: true));

    /// <summary>Runs until <paramref name="stoppingToken"/> is cancelled; a failing <paramref name="send"/> is logged, never fatal.</summary>
    public async Task RunAsync(Func<RealtimeHintMessage, CancellationToken, Task> send, CancellationToken stoppingToken)
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync(stoppingToken))
        {
            await Task.Delay(options.Value.BatchWindow, timeProvider, stoppingToken);

            var events = new List<RealtimeHint>();
            var resync = Interlocked.Exchange(ref _overflowed, 0) == 1;
            while (reader.TryRead(out var item))
            {
                events.AddRange(item.Events);
                resync |= item.Resync;
            }

            if (events.Count > MaxEventsPerMessage)
            {
                events.Clear();
                resync = true;
            }

            try
            {
                await send(new RealtimeHintMessage(events, resync), stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Hints are best effort: clients refetch on their next hint, on reconnect and when a page opens.
                logger.LogWarning(exception, "Hint batch with {EventCount} events could not be sent to the clients", events.Count);
            }
        }
    }

    private void Write(Item item)
    {
        if (!_queue.Writer.TryWrite(item))
        {
            Interlocked.Exchange(ref _overflowed, 1);
        }
    }

    private sealed record Item(IReadOnlyList<RealtimeHint> Events, bool Resync);
}
