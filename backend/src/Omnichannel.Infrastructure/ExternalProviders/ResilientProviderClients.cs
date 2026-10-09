using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Invoices;
using Omnichannel.Application.Notifications;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Omnichannel.Infrastructure.ExternalProviders;

/// <summary>
/// Calls the invoice provider's adapter through its timeout and circuit breaker (<see cref="ProviderResilience"/>): a
/// slow or broken provider neither blocks the Invoice Worker nor is hammered while it is down.
/// </summary>
public sealed class ResilientInvoiceProviderClient(
    [FromKeyedServices(ResilientInvoiceProviderClient.AdapterKey)] IInvoiceProviderClient adapter,
    IOptionsMonitor<ProviderResilienceOptions> options,
    TimeProvider timeProvider,
    ILogger<ResilientInvoiceProviderClient> logger) : IInvoiceProviderClient
{
    /// <summary>Service key of the adapter that talks to the provider (the simulator today, a real adapter later).</summary>
    public const string AdapterKey = "invoice-provider-adapter";

    private readonly ProviderResilienceOptions _options = options.Get(ProviderResilienceOptions.InvoiceProvider);
    private readonly Lazy<ResiliencePipeline> _pipeline = new(() => ProviderResilience.CreatePipeline<InvoiceProviderException>(
        "invoice provider",
        options.Get(ProviderResilienceOptions.InvoiceProvider),
        timeProvider,
        exception => exception.IsTransient,
        logger));

    public async Task<IssuedInvoice> IssueAsync(InvoiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.Value.ExecuteAsync(
                static async (state, token) => await state.adapter.IssueAsync(state.request, token),
                (adapter, request),
                cancellationToken);
        }
        catch (TimeoutRejectedException exception)
        {
            throw new InvoiceProviderException(
                InvoiceErrorCodes.ProviderTimeout,
                $"The invoice provider did not answer within {_options.CallTimeout}.",
                isTransient: true,
                exception);
        }
        catch (BrokenCircuitException exception)
        {
            throw new InvoiceProviderException(
                InvoiceErrorCodes.ProviderCircuitOpen,
                "The invoice provider was not called: its circuit breaker is open after repeated failures.",
                isTransient: true,
                exception);
        }
    }
}

/// <summary>
/// Sends through the mail adapter behind its timeout and circuit breaker (<see cref="ProviderResilience"/>): a slow or
/// broken mail server neither blocks the Notification Worker nor is hammered while it is down.
/// </summary>
public sealed class ResilientEmailSender(
    [FromKeyedServices(ResilientEmailSender.AdapterKey)] IEmailSender adapter,
    IOptionsMonitor<ProviderResilienceOptions> options,
    TimeProvider timeProvider,
    ILogger<ResilientEmailSender> logger) : IEmailSender
{
    /// <summary>Service key of the adapter that sends (the simulator today, an SMTP or API adapter later).</summary>
    public const string AdapterKey = "email-sender-adapter";

    private readonly ProviderResilienceOptions _options = options.Get(ProviderResilienceOptions.EmailSender);
    private readonly Lazy<ResiliencePipeline> _pipeline = new(() => ProviderResilience.CreatePipeline<EmailSendException>(
        "e-mail sender",
        options.Get(ProviderResilienceOptions.EmailSender),
        timeProvider,
        exception => exception.IsTransient,
        logger));

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _pipeline.Value.ExecuteAsync(
                static async (state, token) => await state.adapter.SendAsync(state.message, token),
                (adapter, message),
                cancellationToken);
        }
        catch (TimeoutRejectedException exception)
        {
            throw new EmailSendException(
                EmailErrorCodes.Timeout,
                $"The mail server did not answer within {_options.CallTimeout}.",
                isTransient: true,
                exception);
        }
        catch (BrokenCircuitException exception)
        {
            throw new EmailSendException(
                EmailErrorCodes.CircuitOpen,
                "The mail server was not called: its circuit breaker is open after repeated failures.",
                isTransient: true,
                exception);
        }
    }
}

public static class ExternalProvidersServiceCollectionExtensions
{
    /// <summary>
    /// The invoice provider and the e-mail sender of the Workers host: the simulated adapters behind their timeout and
    /// circuit breaker. The API calls neither. The host configures the two named <see cref="ProviderResilienceOptions"/>.
    /// </summary>
    public static IServiceCollection AddSimulatedPaymentProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<InvoiceSimulationOptions>(configuration.GetSection(InvoiceSimulationOptions.SectionName));
        services.AddKeyedSingleton<IInvoiceProviderClient, SimulatedInvoiceProviderClient>(ResilientInvoiceProviderClient.AdapterKey);
        services.AddSingleton<IInvoiceProviderClient, ResilientInvoiceProviderClient>();

        services.Configure<EmailSimulationOptions>(configuration.GetSection(EmailSimulationOptions.SectionName));
        services.AddKeyedSingleton<IEmailSender, SimulatedEmailSender>(ResilientEmailSender.AdapterKey);
        services.AddSingleton<IEmailSender, ResilientEmailSender>();

        return services;
    }
}
