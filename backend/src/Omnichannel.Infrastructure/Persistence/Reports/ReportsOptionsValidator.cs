using Microsoft.Extensions.Options;
using Omnichannel.Application.Reports;

namespace Omnichannel.Infrastructure.Persistence.Reports;

/// <summary>Stops a host at start-up when a <c>Reports</c> setting is out of range (<see cref="ReportsOptionsRules"/>).</summary>
public sealed class ReportsOptionsValidator : IValidateOptions<ReportsOptions>
{
    public ValidateOptionsResult Validate(string? name, ReportsOptions options)
    {
        var failures = ReportsOptionsRules.Validate(options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
