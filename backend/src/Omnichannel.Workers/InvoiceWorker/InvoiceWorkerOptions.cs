using Microsoft.Extensions.Options;
using Omnichannel.Workers.Processing;

namespace Omnichannel.Workers.InvoiceWorker;

/// <summary>Bound from the <c>InvoiceWorker</c> configuration section.</summary>
public sealed class InvoiceWorkerOptions : ProviderWorkerOptions
{
    public const string SectionName = "InvoiceWorker";
}

public sealed class InvoiceWorkerOptionsValidator : IValidateOptions<InvoiceWorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, InvoiceWorkerOptions options) =>
        options.Validate(InvoiceWorkerOptions.SectionName) is { Count: > 0 } failures
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
}
