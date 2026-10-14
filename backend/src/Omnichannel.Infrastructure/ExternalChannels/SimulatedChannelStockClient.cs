using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Omnichannel.Application.Channels;

namespace Omnichannel.Infrastructure.ExternalChannels;

/// <summary>
/// Stands in for the channel APIs (decision 2026-10-02: no real channel accounts exist). A call is answered according to
/// <see cref="ChannelSimulationOptions"/>, and every call is logged, so retry, backoff and the DLQ can be demonstrated
/// without code changes. The stock value of an <b>accepted</b> push is kept (<see cref="ISimulatedChannelStockStore"/>,
/// decision 2026-10-05) and a read answers from what was kept: that is the channel-side state reconciliation compares
/// with. A real adapter implements <see cref="IChannelStockClient"/> the same way and maps HTTP outcomes to
/// <see cref="ChannelApiException"/>.
/// </summary>
public sealed class SimulatedChannelStockClient(
    IOptionsMonitor<ChannelSimulationOptions> options,
    ISimulatedChannelStockStore store,
    ILogger<SimulatedChannelStockClient> logger) : IChannelStockClient
{
    public async Task PushAvailableStockAsync(ChannelStockUpdate update, CancellationToken cancellationToken)
    {
        // Read per call: a changed settings file applies to the next call.
        var simulation = options.CurrentValue;
        var mode = await ModeAfterLatencyAsync(simulation, update.ChannelSlug, cancellationToken);

        if (mode == ChannelSimulationMode.Timeout)
        {
            LogPush(update, "no answer");
            // Never answers: ends when the caller's timeout (or the host shutting down) cancels the call.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var failure = ModeFailure(update.ChannelSlug, mode, "rejected the update") ?? SkuFailure(update, simulation.SkuRules);
        if (failure is not null)
        {
            LogPush(update, failure.ErrorCode);
            throw failure;
        }

        if (simulation.SkuRules && HasPrefix(update.ExternalSku, ChannelSimulationOptions.LostUpdateSkuPrefix))
        {
            // A channel that loses updates: the caller sees a success, a later read does not show the value.
            LogPush(update, "accepted, not stored");
            return;
        }

        // Only now, and only for a push that is accepted: a push that failed, timed out or was cancelled leaves the
        // channel's value as it was.
        await WithStoreAsync(
            update.ChannelSlug,
            async () =>
            {
                await store.SaveAsync(
                    update.ChannelSlug,
                    SimulatedChannelStock.ExternalKeyOf(update.ExternalProductId, update.ExternalSku),
                    update.AvailableStock,
                    update.RequestId,
                    cancellationToken);
                return true;
            });
        LogPush(update, "accepted");
    }

    public async Task<IReadOnlyList<ChannelStockReading>> ReadAvailableStockAsync(ChannelStockQuery query, CancellationToken cancellationToken)
    {
        var mode = await ModeAfterLatencyAsync(options.CurrentValue, query.ChannelSlug, cancellationToken);

        if (mode == ChannelSimulationMode.Timeout)
        {
            logger.LogInformation("Simulated read of {Channel}: {Count} products: no answer", query.ChannelSlug, query.Items.Count);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        // The SKU rules describe what a channel does with an update; a read is not affected by them.
        if (ModeFailure(query.ChannelSlug, mode, "rejected the read") is { } failure)
        {
            logger.LogInformation(
                "Simulated read of {Channel}: {Count} products: {Outcome}", query.ChannelSlug, query.Items.Count, failure.ErrorCode);
            throw failure;
        }

        var keys = query.Items
            .Select(item => (item.ProductId, Key: SimulatedChannelStock.ExternalKeyOf(item.ExternalProductId, item.ExternalSku)))
            .ToList();
        var stored = await WithStoreAsync(
            query.ChannelSlug,
            () => store.ReadAsync(query.ChannelSlug, keys.Select(k => k.Key).Distinct(StringComparer.Ordinal).ToList(), cancellationToken));

        logger.LogInformation(
            "Simulated read of {Channel}: {Count} products, {Known} with a stock value", query.ChannelSlug, keys.Count, stored.Count);
        return keys
            .Select(k => new ChannelStockReading(k.ProductId, stored.TryGetValue(k.Key, out var stock) ? stock : null))
            .ToList();
    }

    /// <summary>The channel's configured mode, after its configured latency has passed.</summary>
    private static async Task<ChannelSimulationMode> ModeAfterLatencyAsync(
        ChannelSimulationOptions simulation,
        string channelSlug,
        CancellationToken cancellationToken)
    {
        var channel = simulation.For(channelSlug);
        if (!Enum.TryParse<ChannelSimulationMode>(channel.Mode, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            throw new InvalidOperationException(
                $"{ChannelSimulationOptions.SectionName}:Channels:{channelSlug}:Mode '{channel.Mode}' is not one of "
                + $"{string.Join(", ", Enum.GetNames<ChannelSimulationMode>())}.");
        }

        if (channel.LatencyMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(channel.LatencyMs), cancellationToken);
        }

        return mode;
    }

    /// <summary>
    /// The simulator's own storage failed. To its caller that is a channel that is down: transient, like any outage. The
    /// exception itself is not logged and not passed on - its text can name the database server.
    /// </summary>
    private async Task<T> WithStoreAsync<T>(string channelSlug, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Simulated channel {Channel}: its stock store failed ({ExceptionType}, SQL state {SqlState}); answered as unavailable",
                channelSlug,
                exception.GetType().Name,
                (exception as PostgresException)?.SqlState ?? "none");
            throw new ChannelApiException(
                ChannelApiErrorCodes.Unavailable,
                $"Simulated channel '{channelSlug}' is unavailable (its stock store failed).",
                isTransient: true);
        }
    }

    private void LogPush(ChannelStockUpdate update, string outcome) =>
        logger.LogInformation(
            "Simulated push to {Channel}: SKU {Sku} (external product {ExternalProductId}, external SKU {ExternalSku}) available stock {AvailableStock}; task {TaskId}, attempt {AttemptNo}, request {RequestId}: {Outcome}",
            update.ChannelSlug,
            update.Sku,
            update.ExternalProductId,
            update.ExternalSku,
            update.AvailableStock,
            update.TaskId,
            update.AttemptNo,
            update.RequestId,
            outcome);

    private static ChannelApiException? ModeFailure(string channelSlug, ChannelSimulationMode mode, string rejection) =>
        mode switch
        {
            ChannelSimulationMode.TransientError => new ChannelApiException(
                ChannelApiErrorCodes.Unavailable,
                $"Simulated channel '{channelSlug}' is unavailable (mode {mode}).",
                isTransient: true),
            ChannelSimulationMode.PermanentError => new ChannelApiException(
                ChannelApiErrorCodes.Rejected,
                $"Simulated channel '{channelSlug}' {rejection} (mode {mode}).",
                isTransient: false),
            _ => null
        };

    private static ChannelApiException? SkuFailure(ChannelStockUpdate update, bool skuRules)
    {
        if (!skuRules || update.ExternalSku is not { } externalSku)
        {
            return null;
        }

        if (HasPrefix(externalSku, ChannelSimulationOptions.PermanentErrorSkuPrefix))
        {
            return new ChannelApiException(
                ChannelApiErrorCodes.Rejected,
                $"Simulated channel '{update.ChannelSlug}' does not know external SKU '{externalSku}'.",
                isTransient: false);
        }

        if (HasPrefix(externalSku, ChannelSimulationOptions.UnavailableSkuPrefix))
        {
            return new ChannelApiException(
                ChannelApiErrorCodes.Unavailable,
                $"Simulated channel '{update.ChannelSlug}' is unavailable for external SKU '{externalSku}'.",
                isTransient: true);
        }

        if (HasPrefix(externalSku, ChannelSimulationOptions.TransientErrorSkuPrefix) && update.AttemptNo == 1)
        {
            return new ChannelApiException(
                ChannelApiErrorCodes.Unavailable,
                $"Simulated channel '{update.ChannelSlug}' is unavailable for external SKU '{externalSku}' on the first attempt.",
                isTransient: true);
        }

        return null;
    }

    private static bool HasPrefix(string? externalSku, string prefix) =>
        externalSku is not null && externalSku.StartsWith(prefix, StringComparison.Ordinal);
}
