using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Invoices;

namespace Omnichannel.Infrastructure.ExternalProviders;

/// <summary>Bound from the <c>InvoiceSimulation</c> configuration section; only the simulated invoice provider reads it.</summary>
public sealed class InvoiceSimulationOptions : ProviderSimulationOptions
{
    public const string SectionName = "InvoiceSimulation";

    /// <summary>External order id prefix the simulator refuses for good (<c>invoice_provider_rejected</c>) when <c>Rules</c> is on.</summary>
    public const string PermanentErrorOrderPrefix = "SIM-INV-PERM-";

    /// <summary>
    /// External order id prefix that fails with <c>invoice_provider_unavailable</c> on attempt 1 only and succeeds
    /// afterwards, when <c>Rules</c> is on.
    /// </summary>
    public const string TransientErrorOrderPrefix = "SIM-INV-TEMP-";
}

/// <summary>
/// Stands in for the invoice provider (decision 2026-10-09: no provider account exists). A call is answered according
/// to <see cref="InvoiceSimulationOptions"/> and logged, so retry, backoff and the DLQ can be demonstrated without code
/// changes. The simulator keeps <b>no state</b>: the number of an invoice is derived from the idempotency key of the
/// request, so a second call for one order answers with the same number - what a real provider does with the key.
/// </summary>
public sealed class SimulatedInvoiceProviderClient(
    IOptionsMonitor<InvoiceSimulationOptions> options,
    TimeProvider timeProvider,
    ILogger<SimulatedInvoiceProviderClient> logger) : IInvoiceProviderClient
{
    public async Task<IssuedInvoice> IssueAsync(InvoiceRequest request, CancellationToken cancellationToken)
    {
        // Read per call: a changed settings file applies to the next call.
        var simulation = options.CurrentValue;
        var mode = await simulation.ModeAfterLatencyAsync(InvoiceSimulationOptions.SectionName, cancellationToken);

        if (mode == ProviderSimulationMode.Timeout)
        {
            Log(request, "no answer");
            // Never answers: ends when the caller's timeout (or the host shutting down) cancels the call.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if ((ModeFailure(mode) ?? RuleFailure(request, simulation.Rules)) is { } failure)
        {
            Log(request, failure.ErrorCode);
            throw failure;
        }

        var issued = new IssuedInvoice(InvoiceNumberOf(request.Key, request.PaidAt), timeProvider.GetUtcNow());
        Log(request, $"issued {issued.InvoiceNumber}");
        return issued;
    }

    /// <summary>
    /// <c>SIM-{yyyyMM}-{8 hex}</c>: the month the order was paid in (UTC) and the start of the SHA-256 of the key. The
    /// same key and payment time always give the same number.
    /// </summary>
    public static string InvoiceNumberOf(string key, DateTimeOffset paidAt)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return $"SIM-{paidAt.UtcDateTime.ToString("yyyyMM", CultureInfo.InvariantCulture)}-{hash[..8]}";
    }

    // Neither the customer nor the lines: the log needs to say which order, not whose.
    private void Log(InvoiceRequest request, string outcome) =>
        logger.LogInformation(
            "Simulated invoice provider: order {OrderId} ({ExternalOrderId}), total {Total}, key {Key}, attempt {AttemptNo}: {Outcome}",
            request.OrderId,
            request.ExternalOrderId,
            request.Total,
            request.Key,
            request.AttemptNo,
            outcome);

    private static InvoiceProviderException? ModeFailure(ProviderSimulationMode mode) =>
        mode switch
        {
            ProviderSimulationMode.TransientError => new InvoiceProviderException(
                InvoiceErrorCodes.ProviderUnavailable,
                $"The simulated invoice provider is unavailable (mode {mode}).",
                isTransient: true),
            ProviderSimulationMode.PermanentError => new InvoiceProviderException(
                InvoiceErrorCodes.ProviderRejected,
                $"The simulated invoice provider refused the invoice (mode {mode}).",
                isTransient: false),
            _ => null
        };

    private static InvoiceProviderException? RuleFailure(InvoiceRequest request, bool rules)
    {
        if (!rules)
        {
            return null;
        }

        if (request.ExternalOrderId.StartsWith(InvoiceSimulationOptions.PermanentErrorOrderPrefix, StringComparison.Ordinal))
        {
            return new InvoiceProviderException(
                InvoiceErrorCodes.ProviderRejected,
                $"The simulated invoice provider refuses orders whose id starts with {InvoiceSimulationOptions.PermanentErrorOrderPrefix}.",
                isTransient: false);
        }

        if (request.ExternalOrderId.StartsWith(InvoiceSimulationOptions.TransientErrorOrderPrefix, StringComparison.Ordinal)
            && request.AttemptNo == 1)
        {
            return new InvoiceProviderException(
                InvoiceErrorCodes.ProviderUnavailable,
                $"The simulated invoice provider is unavailable on the first attempt for orders whose id starts with {InvoiceSimulationOptions.TransientErrorOrderPrefix}.",
                isTransient: true);
        }

        return null;
    }
}
