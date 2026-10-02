using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Channels;

namespace Omnichannel.Infrastructure.ExternalChannels;

/// <summary>
/// Stands in for the channel APIs (decision 2026-10-02: no real channel accounts exist). It keeps no state: a push is
/// accepted or fails according to <see cref="ChannelSimulationOptions"/>, and every push is logged, so retry, backoff and the
/// DLQ can be demonstrated without code changes. A real adapter implements <see cref="IChannelStockClient"/> the same way and
/// maps HTTP outcomes to <see cref="ChannelApiException"/>.
/// </summary>
public sealed class SimulatedChannelStockClient(
    IOptionsMonitor<ChannelSimulationOptions> options,
    ILogger<SimulatedChannelStockClient> logger) : IChannelStockClient
{
    public async Task PushAvailableStockAsync(ChannelStockUpdate update, CancellationToken cancellationToken)
    {
        // Read per push: a changed settings file applies to the next call.
        var simulation = options.CurrentValue;
        var channel = simulation.For(update.ChannelSlug);
        if (!Enum.TryParse<ChannelSimulationMode>(channel.Mode, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            throw new InvalidOperationException(
                $"{ChannelSimulationOptions.SectionName}:Channels:{update.ChannelSlug}:Mode '{channel.Mode}' is not one of "
                + $"{string.Join(", ", Enum.GetNames<ChannelSimulationMode>())}.");
        }

        if (channel.LatencyMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(channel.LatencyMs), cancellationToken);
        }

        var failure = FailureOf(update, mode, simulation.SkuRules);
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
            mode == ChannelSimulationMode.Timeout ? "no answer" : failure?.ErrorCode ?? "accepted");

        if (mode == ChannelSimulationMode.Timeout)
        {
            // Never answers: ends when the caller's timeout (or the host shutting down) cancels the call.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static ChannelApiException? FailureOf(ChannelStockUpdate update, ChannelSimulationMode mode, bool skuRules)
    {
        switch (mode)
        {
            case ChannelSimulationMode.TransientError:
                return new ChannelApiException(
                    ChannelApiErrorCodes.Unavailable,
                    $"Simulated channel '{update.ChannelSlug}' is unavailable (mode {mode}).",
                    isTransient: true);
            case ChannelSimulationMode.PermanentError:
                return new ChannelApiException(
                    ChannelApiErrorCodes.Rejected,
                    $"Simulated channel '{update.ChannelSlug}' rejected the update (mode {mode}).",
                    isTransient: false);
        }

        if (!skuRules || update.ExternalSku is not { } externalSku)
        {
            return null;
        }

        if (externalSku.StartsWith(ChannelSimulationOptions.PermanentErrorSkuPrefix, StringComparison.Ordinal))
        {
            return new ChannelApiException(
                ChannelApiErrorCodes.Rejected,
                $"Simulated channel '{update.ChannelSlug}' does not know external SKU '{externalSku}'.",
                isTransient: false);
        }

        if (externalSku.StartsWith(ChannelSimulationOptions.TransientErrorSkuPrefix, StringComparison.Ordinal) && update.AttemptNo == 1)
        {
            return new ChannelApiException(
                ChannelApiErrorCodes.Unavailable,
                $"Simulated channel '{update.ChannelSlug}' is unavailable for external SKU '{externalSku}' on the first attempt.",
                isTransient: true);
        }

        return null;
    }
}
