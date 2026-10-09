using Microsoft.Extensions.Options;
using Omnichannel.Application.Notifications;
using Omnichannel.Application.Reports;
using Omnichannel.Workers.Processing;

namespace Omnichannel.Workers.NotificationWorker;

/// <summary>Bound from the <c>NotificationWorker</c> configuration section.</summary>
public sealed class NotificationWorkerOptions : ProviderWorkerOptions
{
    public const string SectionName = "NotificationWorker";
}

public sealed class NotificationWorkerOptionsValidator : IValidateOptions<NotificationWorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, NotificationWorkerOptions options) =>
        options.Validate(NotificationWorkerOptions.SectionName) is { Count: > 0 } failures
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
}

/// <summary>
/// The <c>Notifications</c> settings are checked at start-up: an e-mail in a language without texts, or with times of a
/// zone this host does not know, must stop the host and not fail every e-mail task.
/// </summary>
public sealed class NotificationsOptionsValidator : IValidateOptions<NotificationsOptions>
{
    public ValidateOptionsResult Validate(string? name, NotificationsOptions options)
    {
        var failures = new List<string>();

        if (options.Language is not (NotificationsOptions.Vietnamese or NotificationsOptions.English))
        {
            failures.Add(
                $"{NotificationsOptions.SectionName}:Language must be '{NotificationsOptions.Vietnamese}' or '{NotificationsOptions.English}'.");
        }

        if (!ReportTimeZones.IsKnown(options.TimeZone))
        {
            failures.Add($"{NotificationsOptions.SectionName}:TimeZone must be an IANA time zone id this host knows.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
