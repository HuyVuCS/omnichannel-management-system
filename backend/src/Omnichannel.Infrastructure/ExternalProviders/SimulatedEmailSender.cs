using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Notifications;

namespace Omnichannel.Infrastructure.ExternalProviders;

/// <summary>Bound from the <c>EmailSimulation</c> configuration section; only the simulated e-mail sender reads it.</summary>
public sealed class EmailSimulationOptions : ProviderSimulationOptions
{
    public const string SectionName = "EmailSimulation";

    /// <summary>Recipient domain the simulator refuses for good (<c>email_rejected</c>) when <c>Rules</c> is on.</summary>
    public const string RejectedDomain = "sim-reject.test";

    /// <summary>
    /// Recipient domain that fails with <c>email_unavailable</c> on attempt 1 only and succeeds afterwards, when
    /// <c>Rules</c> is on.
    /// </summary>
    public const string TransientErrorDomain = "sim-temp.test";
}

/// <summary>
/// Stands in for a mail server (decision 2026-10-09: the stack has no mail infrastructure). A message is answered
/// according to <see cref="EmailSimulationOptions"/> and "sent" by writing one log line: the subject and the
/// <b>masked</b> address - never the text of the message, never the full address. Nothing is stored; the task and its
/// attempts are the record of an e-mail.
/// </summary>
public sealed class SimulatedEmailSender(
    IOptionsMonitor<EmailSimulationOptions> options,
    ILogger<SimulatedEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        // Read per call: a changed settings file applies to the next call.
        var simulation = options.CurrentValue;
        var mode = await simulation.ModeAfterLatencyAsync(EmailSimulationOptions.SectionName, cancellationToken);

        if (mode == ProviderSimulationMode.Timeout)
        {
            Log(message, "no answer");
            // Never answers: ends when the caller's timeout (or the host shutting down) cancels the call.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if ((ModeFailure(mode) ?? RuleFailure(message, simulation.Rules)) is { } failure)
        {
            Log(message, failure.ErrorCode);
            throw failure;
        }

        Log(message, "sent");
    }

    private void Log(EmailMessage message, string outcome) =>
        logger.LogInformation(
            "Simulated e-mail to {Recipient}: {Subject}, key {Key}, attempt {AttemptNo}: {Outcome}",
            EmailAddress.Mask(message.To),
            message.Subject,
            message.Key,
            message.AttemptNo,
            outcome);

    private static EmailSendException? ModeFailure(ProviderSimulationMode mode) =>
        mode switch
        {
            ProviderSimulationMode.TransientError => new EmailSendException(
                EmailErrorCodes.Unavailable,
                $"The simulated mail server is unavailable (mode {mode}).",
                isTransient: true),
            ProviderSimulationMode.PermanentError => new EmailSendException(
                EmailErrorCodes.Rejected,
                $"The simulated mail server refused the message (mode {mode}).",
                isTransient: false),
            _ => null
        };

    private static EmailSendException? RuleFailure(EmailMessage message, bool rules)
    {
        if (!rules)
        {
            return null;
        }

        var domain = EmailAddress.DomainOf(message.To);
        if (domain == EmailSimulationOptions.RejectedDomain)
        {
            return new EmailSendException(
                EmailErrorCodes.Rejected,
                $"The simulated mail server refuses recipients at {EmailSimulationOptions.RejectedDomain}.",
                isTransient: false);
        }

        if (domain == EmailSimulationOptions.TransientErrorDomain && message.AttemptNo == 1)
        {
            return new EmailSendException(
                EmailErrorCodes.Unavailable,
                $"The simulated mail server is unavailable on the first attempt for recipients at {EmailSimulationOptions.TransientErrorDomain}.",
                isTransient: true);
        }

        return null;
    }
}
