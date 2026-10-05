using Microsoft.Extensions.Options;
using Omnichannel.Application.Channels;

namespace Omnichannel.Infrastructure.Persistence.Channels;

/// <summary>Stops the host at start-up when a <c>Reconciliation</c> setting is out of range (<see cref="ReconciliationOptionsRules"/>).</summary>
public sealed class ReconciliationOptionsValidator : IValidateOptions<ReconciliationOptions>
{
    public ValidateOptionsResult Validate(string? name, ReconciliationOptions options)
    {
        var failures = ReconciliationOptionsRules.Validate(options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
