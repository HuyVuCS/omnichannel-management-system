using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Invoices;
using Omnichannel.Application.Notifications;
using Omnichannel.Infrastructure.ExternalChannels;
using Omnichannel.Infrastructure.ExternalProviders;
using Omnichannel.Tests.Integration.Workers;
using Omnichannel.Workers.Processing;

namespace Omnichannel.Tests.Workers;

/// <summary>
/// Task 024: the simulated invoice provider and e-mail sender (modes, latency, Development rules, what they log) and
/// the timeout and circuit breaker in front of them.
/// </summary>
public sealed class PaymentProviderTests
{
    private static readonly DateTimeOffset PaidAt = new(2026, 10, 9, 8, 30, 0, TimeSpan.FromHours(7));

    // ---- the simulated invoice provider ------------------------------------------------------------------------

    [Fact]
    public async Task The_invoice_provider_issues_by_default_and_logs_the_order_without_the_customer()
    {
        var logs = new CapturingLoggerProvider();
        var provider = new SimulatedInvoiceProviderClient(
            Monitor(new InvoiceSimulationOptions()), TimeProvider.System, new Logger<SimulatedInvoiceProviderClient>(new LoggerFactory([logs])));
        var request = Request("WEB-1001");
        var before = DateTimeOffset.UtcNow;

        var issued = await provider.IssueAsync(request, CancellationToken.None);

        Assert.Equal(SimulatedInvoiceProviderClient.InvoiceNumberOf(request.Key, PaidAt), issued.InvoiceNumber);
        Assert.InRange(issued.IssuedAt, before, DateTimeOffset.UtcNow);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(request.OrderId.ToString(), entry.Message);
        Assert.Contains("WEB-1001", entry.Message);
        Assert.Contains(request.Key, entry.Message);
        Assert.Contains("attempt 1", entry.Message);
        Assert.EndsWith($"issued {issued.InvoiceNumber}", entry.Message);
        Assert.DoesNotContain("Nguyen Van A", entry.Message);
        Assert.DoesNotContain("Green tea", entry.Message);
    }

    [Fact]
    public void The_invoice_number_is_the_month_of_the_payment_and_the_start_of_the_hash_of_the_key()
    {
        var number = SimulatedInvoiceProviderClient.InvoiceNumberOf("invoice:0199c1f0-0000-7000-8000-000000000001", PaidAt);

        Assert.Matches("^SIM-202610-[0-9A-F]{8}$", number);
        // The same key gives the same number, whenever it is asked for; another key another number.
        Assert.Equal(number, SimulatedInvoiceProviderClient.InvoiceNumberOf("invoice:0199c1f0-0000-7000-8000-000000000001", PaidAt));
        Assert.NotEqual(number, SimulatedInvoiceProviderClient.InvoiceNumberOf("invoice:0199c1f0-0000-7000-8000-000000000002", PaidAt));
        Assert.Equal(number[10..], SimulatedInvoiceProviderClient.InvoiceNumberOf("invoice:0199c1f0-0000-7000-8000-000000000001", PaidAt.AddMonths(1))[10..]);
        // The month in UTC: 2026-11-01 02:00 at +07:00 is still October.
        Assert.StartsWith("SIM-202610-", SimulatedInvoiceProviderClient.InvoiceNumberOf("k", new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.FromHours(7))));
        Assert.StartsWith("SIM-202611-", SimulatedInvoiceProviderClient.InvoiceNumberOf("k", new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.FromHours(7))));
        Assert.True(number.Length <= Omnichannel.Domain.Entities.Invoice.MaxInvoiceNumberLength);
    }

    [Fact]
    public async Task A_second_call_for_one_key_answers_with_the_same_number_without_any_state()
    {
        var request = Request("WEB-1001");

        // Two providers that never met (two Workers instances, or one after a restart).
        var first = await InvoiceProvider(new InvoiceSimulationOptions()).IssueAsync(request, CancellationToken.None);
        var second = await InvoiceProvider(new InvoiceSimulationOptions()).IssueAsync(request with { AttemptNo = 2 }, CancellationToken.None);

        Assert.Equal(first.InvoiceNumber, second.InvoiceNumber);
    }

    [Theory]
    [InlineData("TransientError", InvoiceErrorCodes.ProviderUnavailable, true)]
    [InlineData("transienterror", InvoiceErrorCodes.ProviderUnavailable, true)]
    [InlineData("PermanentError", InvoiceErrorCodes.ProviderRejected, false)]
    public async Task The_error_modes_of_the_invoice_provider_fail_with_their_code(string mode, string errorCode, bool transient)
    {
        var logs = new CapturingLoggerProvider();
        var provider = new SimulatedInvoiceProviderClient(
            Monitor(new InvoiceSimulationOptions { Mode = mode }), TimeProvider.System, new Logger<SimulatedInvoiceProviderClient>(new LoggerFactory([logs])));

        var exception = await Assert.ThrowsAsync<InvoiceProviderException>(() => provider.IssueAsync(Request("WEB-1"), CancellationToken.None));

        Assert.Equal((errorCode, transient), (exception.ErrorCode, exception.IsTransient));
        Assert.EndsWith(errorCode, Assert.Single(logs.Entries).Message);
    }

    [Fact]
    public async Task The_rules_of_the_invoice_provider_fail_single_orders_only_while_they_are_on()
    {
        var provider = InvoiceProvider(new InvoiceSimulationOptions { Rules = true });

        var rejected = await Assert.ThrowsAsync<InvoiceProviderException>(() => provider.IssueAsync(Request("SIM-INV-PERM-1"), CancellationToken.None));
        Assert.Equal((InvoiceErrorCodes.ProviderRejected, false), (rejected.ErrorCode, rejected.IsTransient));
        // Rejected on every attempt.
        await Assert.ThrowsAsync<InvoiceProviderException>(() => provider.IssueAsync(Request("SIM-INV-PERM-1", attemptNo: 4), CancellationToken.None));

        var unavailable = await Assert.ThrowsAsync<InvoiceProviderException>(() => provider.IssueAsync(Request("SIM-INV-TEMP-1"), CancellationToken.None));
        Assert.Equal((InvoiceErrorCodes.ProviderUnavailable, true), (unavailable.ErrorCode, unavailable.IsTransient));
        await provider.IssueAsync(Request("SIM-INV-TEMP-1", attemptNo: 2), CancellationToken.None);

        // Other orders are not affected; the prefix is case-sensitive and a prefix.
        await provider.IssueAsync(Request("WEB-SIM-INV-PERM-1"), CancellationToken.None);
        await provider.IssueAsync(Request("sim-inv-perm-1"), CancellationToken.None);

        // Off (the default, everywhere but Development): an order id like any other.
        var withoutRules = InvoiceProvider(new InvoiceSimulationOptions());
        await withoutRules.IssueAsync(Request("SIM-INV-PERM-1"), CancellationToken.None);
        await withoutRules.IssueAsync(Request("SIM-INV-TEMP-1"), CancellationToken.None);
        Assert.False(new InvoiceSimulationOptions().Rules);
    }

    [Fact]
    public async Task The_timeout_mode_never_answers_until_the_call_is_cancelled_and_latency_delays_an_answer()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvoiceProvider(new InvoiceSimulationOptions { Mode = "Timeout" }).IssueAsync(Request("WEB-1"), cancellation.Token));

        var watch = Stopwatch.StartNew();
        await InvoiceProvider(new InvoiceSimulationOptions { LatencyMs = 300 }).IssueAsync(Request("WEB-1"), CancellationToken.None);
        Assert.True(watch.ElapsedMilliseconds >= 250, $"answered after {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_settings_change_applies_to_the_next_call_and_an_unknown_mode_fails_with_a_clear_message()
    {
        var monitor = Monitor(new InvoiceSimulationOptions { Mode = "TransientError" });
        var provider = new SimulatedInvoiceProviderClient(monitor, TimeProvider.System, NullLogger<SimulatedInvoiceProviderClient>.Instance);
        await Assert.ThrowsAsync<InvoiceProviderException>(() => provider.IssueAsync(Request("WEB-1"), CancellationToken.None));

        monitor.CurrentValue = new InvoiceSimulationOptions();
        await provider.IssueAsync(Request("WEB-1"), CancellationToken.None);

        monitor.CurrentValue = new InvoiceSimulationOptions { Mode = "Sometimes" };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.IssueAsync(Request("WEB-1"), CancellationToken.None));
        Assert.Contains("InvoiceSimulation:Mode 'Sometimes'", exception.Message);
        Assert.Contains("Succeed, TransientError, PermanentError, Timeout", exception.Message);
    }

    // ---- the simulated e-mail sender ---------------------------------------------------------------------------

    [Fact]
    public async Task The_sender_logs_the_subject_and_a_masked_address_and_never_the_text_or_the_full_address()
    {
        var logs = new CapturingLoggerProvider();
        var sender = new SimulatedEmailSender(Monitor(new EmailSimulationOptions()), new Logger<SimulatedEmailSender>(new LoggerFactory([logs])));

        await sender.SendAsync(Email("anna.nguyen@example.com"), CancellationToken.None);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Simulated e-mail to a***@example.com", entry.Message);
        Assert.Contains(": Payment received for order WEB-1, key email:payment:", entry.Message);
        Assert.Contains("email:payment:", entry.Message);
        Assert.EndsWith("sent", entry.Message);
        Assert.DoesNotContain("anna.nguyen", entry.Message);
        Assert.DoesNotContain("SECRET-BODY", entry.Message);
        // Nothing of it in the structured values either.
        Assert.DoesNotContain(logs.Entries, e => e.Scope.Values.Any(v => v is string text && text.Contains("anna.nguyen", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("anna@example.com", "a***@example.com")]
    [InlineData("A@B.vn", "A***@B.vn")]
    [InlineData("first.last+tag@mail.example.org", "f***@mail.example.org")]
    // The last @ separates the domain.
    [InlineData("\"odd@local\"@example.com", "\"***@example.com")]
    // Not local@domain: nothing of it is shown.
    [InlineData("@example.com", "***")]
    [InlineData("anna@", "***")]
    [InlineData("anna", "***")]
    [InlineData("", "***")]
    [InlineData(null, "***")]
    public void A_masked_address_keeps_the_first_character_and_the_domain(string? address, string expected)
    {
        Assert.Equal(expected, EmailAddress.Mask(address));
    }

    [Theory]
    [InlineData("anna@Example.COM", "example.com")]
    [InlineData("a@b@sim-reject.test", "sim-reject.test")]
    [InlineData("anna", "")]
    [InlineData(null, "")]
    public void The_domain_of_an_address_is_what_follows_the_last_at_sign(string? address, string expected)
    {
        Assert.Equal(expected, EmailAddress.DomainOf(address));
    }

    [Theory]
    [InlineData("TransientError", EmailErrorCodes.Unavailable, true)]
    [InlineData("PermanentError", EmailErrorCodes.Rejected, false)]
    public async Task The_error_modes_of_the_sender_fail_with_their_code_and_log_no_address(string mode, string errorCode, bool transient)
    {
        var logs = new CapturingLoggerProvider();
        var sender = new SimulatedEmailSender(Monitor(new EmailSimulationOptions { Mode = mode }), new Logger<SimulatedEmailSender>(new LoggerFactory([logs])));

        var exception = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Email("anna.nguyen@example.com"), CancellationToken.None));

        Assert.Equal((errorCode, transient), (exception.ErrorCode, exception.IsTransient));
        Assert.DoesNotContain("anna.nguyen", exception.Message);
        Assert.EndsWith(errorCode, Assert.Single(logs.Entries).Message);
        Assert.DoesNotContain("anna.nguyen", logs.Entries[0].Message);
    }

    [Fact]
    public async Task The_rules_of_the_sender_fail_single_recipients_only_while_they_are_on()
    {
        var sender = Sender(new EmailSimulationOptions { Rules = true });

        var rejected = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Email("anna.nguyen@sim-reject.test"), CancellationToken.None));
        Assert.Equal((EmailErrorCodes.Rejected, false), (rejected.ErrorCode, rejected.IsTransient));
        Assert.DoesNotContain("anna.nguyen", rejected.Message);
        await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Email("b@SIM-REJECT.test", attemptNo: 3), CancellationToken.None));

        var unavailable = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Email("c@sim-temp.test"), CancellationToken.None));
        Assert.Equal((EmailErrorCodes.Unavailable, true), (unavailable.ErrorCode, unavailable.IsTransient));
        await sender.SendAsync(Email("c@sim-temp.test", attemptNo: 2), CancellationToken.None);

        // The whole domain, not a part of it.
        await sender.SendAsync(Email("d@not-sim-reject.test"), CancellationToken.None);
        await sender.SendAsync(Email("sim-reject.test@example.com"), CancellationToken.None);

        var withoutRules = Sender(new EmailSimulationOptions());
        await withoutRules.SendAsync(Email("anna@sim-reject.test"), CancellationToken.None);
        await withoutRules.SendAsync(Email("anna@sim-temp.test"), CancellationToken.None);
    }

    [Fact]
    public async Task The_timeout_mode_of_the_sender_never_answers_until_the_call_is_cancelled()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Sender(new EmailSimulationOptions { Mode = "Timeout" }).SendAsync(Email("anna@example.com"), cancellation.Token));
    }

    // ---- timeout and circuit breaker ---------------------------------------------------------------------------

    [Fact]
    public async Task A_provider_that_does_not_answer_in_time_is_a_timeout_of_the_invoice_provider()
    {
        var adapter = new CountingInvoiceProvider(InvoiceProvider(new InvoiceSimulationOptions { Mode = "Timeout" }));
        var client = ResilientInvoice(adapter, new ProviderResilienceOptions { CallTimeout = TimeSpan.FromMilliseconds(200) });

        var watch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<InvoiceProviderException>(() => client.IssueAsync(Request("WEB-1"), CancellationToken.None));

        Assert.Equal((InvoiceErrorCodes.ProviderTimeout, true), (exception.ErrorCode, exception.IsTransient));
        Assert.True(watch.ElapsedMilliseconds < 5_000, $"gave up after {watch.ElapsedMilliseconds} ms");
        // No retry inside a call: every try is an attempt of the task.
        Assert.Equal(1, adapter.Calls);
    }

    [Fact]
    public async Task Repeated_temporary_failures_open_the_breaker_of_the_invoice_provider_and_a_refusal_does_not_count()
    {
        var monitor = Monitor(new InvoiceSimulationOptions { Mode = "PermanentError" });
        var adapter = new CountingInvoiceProvider(new SimulatedInvoiceProviderClient(monitor, TimeProvider.System, NullLogger<SimulatedInvoiceProviderClient>.Instance));
        var client = ResilientInvoice(adapter, Breaker(minimumThroughput: 3));

        // Refusals say nothing about the provider's health: the breaker stays closed however many there are.
        for (var call = 0; call < 6; call++)
        {
            var refused = await Assert.ThrowsAsync<InvoiceProviderException>(() => client.IssueAsync(Request("WEB-1"), CancellationToken.None));
            Assert.Equal(InvoiceErrorCodes.ProviderRejected, refused.ErrorCode);
        }

        Assert.Equal(6, adapter.Calls);

        monitor.CurrentValue = new InvoiceSimulationOptions { Mode = "TransientError" };
        string? last = null;
        for (var call = 0; call < 10 && last != InvoiceErrorCodes.ProviderCircuitOpen; call++)
        {
            last = (await Assert.ThrowsAsync<InvoiceProviderException>(() => client.IssueAsync(Request("WEB-1"), CancellationToken.None))).ErrorCode;
        }

        Assert.Equal(InvoiceErrorCodes.ProviderCircuitOpen, last);
        var callsWhenOpen = adapter.Calls;

        // Open: the provider is not called at all, and the failure is temporary (the task is retried later).
        var open = await Assert.ThrowsAsync<InvoiceProviderException>(() => client.IssueAsync(Request("WEB-1"), CancellationToken.None));
        Assert.Equal((InvoiceErrorCodes.ProviderCircuitOpen, true), (open.ErrorCode, open.IsTransient));
        Assert.Equal(callsWhenOpen, adapter.Calls);
    }

    [Fact]
    public async Task The_sender_has_its_own_timeout_and_breaker_with_its_own_codes()
    {
        var slow = new CountingEmailSender(Sender(new EmailSimulationOptions { Mode = "Timeout" }));
        var timedOut = await Assert.ThrowsAsync<EmailSendException>(() =>
            ResilientEmail(slow, new ProviderResilienceOptions { CallTimeout = TimeSpan.FromMilliseconds(200) }).SendAsync(Email("anna@example.com"), CancellationToken.None));
        Assert.Equal((EmailErrorCodes.Timeout, true), (timedOut.ErrorCode, timedOut.IsTransient));
        Assert.Equal(1, slow.Calls);

        var down = new CountingEmailSender(Sender(new EmailSimulationOptions { Mode = "TransientError" }));
        var client = ResilientEmail(down, Breaker(minimumThroughput: 3));
        string? last = null;
        for (var call = 0; call < 10 && last != EmailErrorCodes.CircuitOpen; call++)
        {
            last = (await Assert.ThrowsAsync<EmailSendException>(() => client.SendAsync(Email("anna@example.com"), CancellationToken.None))).ErrorCode;
        }

        Assert.Equal(EmailErrorCodes.CircuitOpen, last);
        var callsWhenOpen = down.Calls;
        var open = await Assert.ThrowsAsync<EmailSendException>(() => client.SendAsync(Email("anna@example.com"), CancellationToken.None));
        Assert.Equal((EmailErrorCodes.CircuitOpen, true), (open.ErrorCode, open.IsTransient));
        Assert.Equal(callsWhenOpen, down.Calls);
        Assert.DoesNotContain("anna", open.Message);
    }

    [Fact]
    public async Task The_two_providers_do_not_share_a_breaker_or_a_timeout()
    {
        // Each client reads the options of its own name: the sender's short timeout is not the invoice provider's.
        var options = new NamedOptions(new Dictionary<string, ProviderResilienceOptions>
        {
            [ProviderResilienceOptions.EmailSender] = new() { CallTimeout = TimeSpan.FromMilliseconds(150) },
            [ProviderResilienceOptions.InvoiceProvider] = new() { CallTimeout = TimeSpan.FromSeconds(30) }
        });
        var invoices = new ResilientInvoiceProviderClient(
            InvoiceProvider(new InvoiceSimulationOptions { LatencyMs = 600 }), options, TimeProvider.System, NullLogger<ResilientInvoiceProviderClient>.Instance);
        var emails = new ResilientEmailSender(
            Sender(new EmailSimulationOptions { LatencyMs = 600 }), options, TimeProvider.System, NullLogger<ResilientEmailSender>.Instance);

        await invoices.IssueAsync(Request("WEB-1"), CancellationToken.None);
        var timedOut = await Assert.ThrowsAsync<EmailSendException>(() => emails.SendAsync(Email("anna@example.com"), CancellationToken.None));
        Assert.Equal(EmailErrorCodes.Timeout, timedOut.ErrorCode);
    }

    [Fact]
    public async Task A_cancelled_call_is_a_cancellation_not_a_failure_of_the_provider()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var client = ResilientInvoice(
            new CountingInvoiceProvider(InvoiceProvider(new InvoiceSimulationOptions { Mode = "Timeout" })),
            new ProviderResilienceOptions { CallTimeout = TimeSpan.FromSeconds(30) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.IssueAsync(Request("WEB-1"), cancellation.Token));
    }

    // ---- the classifier ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(InvoiceErrorCodes.ProviderTimeout, true)]
    [InlineData(InvoiceErrorCodes.ProviderUnavailable, true)]
    [InlineData(InvoiceErrorCodes.ProviderCircuitOpen, true)]
    [InlineData(InvoiceErrorCodes.ProviderRejected, false)]
    public void An_answer_of_the_invoice_provider_keeps_its_code_and_says_itself_whether_a_retry_can_help(string errorCode, bool transient)
    {
        var error = ProcessingErrorClassifier.Classify(new InvoiceProviderException(errorCode, "the provider said no; password=hunter2", transient));

        Assert.Equal((errorCode, !transient), (error.ErrorCode, error.IsPermanent));
        Assert.DoesNotContain("hunter2", error.ErrorMessage);
    }

    [Theory]
    [InlineData(EmailErrorCodes.Timeout, true)]
    [InlineData(EmailErrorCodes.Unavailable, true)]
    [InlineData(EmailErrorCodes.CircuitOpen, true)]
    [InlineData(EmailErrorCodes.Rejected, false)]
    public void An_answer_of_the_sender_keeps_its_code_and_says_itself_whether_a_retry_can_help(string errorCode, bool transient)
    {
        // Also when something wrapped it.
        var error = ProcessingErrorClassifier.Classify(new InvalidOperationException("outer", new EmailSendException(errorCode, "the mail server said no", transient)));

        Assert.Equal((errorCode, !transient, "the mail server said no"), (error.ErrorCode, error.IsPermanent, error.ErrorMessage));
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static InvoiceRequest Request(string externalOrderId, int attemptNo = 1)
    {
        var orderId = Guid.NewGuid();
        return new InvoiceRequest(
            $"invoice:{orderId}",
            orderId,
            externalOrderId,
            "Nguyen Van A",
            [new InvoiceLine("TEA-1", "Green tea", 2, 75_000m)],
            150_000m,
            PaidAt,
            attemptNo);
    }

    private static EmailMessage Email(string to, int attemptNo = 1) =>
        new($"email:payment:{Guid.NewGuid()}", to, "Payment received for order WEB-1", "SECRET-BODY of the message", attemptNo);

    private static SimulatedInvoiceProviderClient InvoiceProvider(InvoiceSimulationOptions options) =>
        new(Monitor(options), TimeProvider.System, NullLogger<SimulatedInvoiceProviderClient>.Instance);

    private static SimulatedEmailSender Sender(EmailSimulationOptions options) =>
        new(Monitor(options), NullLogger<SimulatedEmailSender>.Instance);

    private static ResilientInvoiceProviderClient ResilientInvoice(IInvoiceProviderClient adapter, ProviderResilienceOptions options) =>
        new(
            adapter,
            new NamedOptions(new Dictionary<string, ProviderResilienceOptions> { [ProviderResilienceOptions.InvoiceProvider] = options }),
            TimeProvider.System,
            NullLogger<ResilientInvoiceProviderClient>.Instance);

    private static ResilientEmailSender ResilientEmail(IEmailSender adapter, ProviderResilienceOptions options) =>
        new(
            adapter,
            new NamedOptions(new Dictionary<string, ProviderResilienceOptions> { [ProviderResilienceOptions.EmailSender] = options }),
            TimeProvider.System,
            NullLogger<ResilientEmailSender>.Instance);

    private static ProviderResilienceOptions Breaker(int minimumThroughput) => new()
    {
        CallTimeout = TimeSpan.FromSeconds(5),
        CircuitBreaker = new ChannelCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = minimumThroughput,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(30)
        }
    };

    private static MutableOptionsMonitor<TOptions> Monitor<TOptions>(TOptions options) => new() { CurrentValue = options };

    private sealed class MutableOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    {
        public required TOptions CurrentValue { get; set; }

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }

    /// <summary>Options by name; a name nobody configured gets the defaults (as the options framework answers).</summary>
    private sealed class NamedOptions(IReadOnlyDictionary<string, ProviderResilienceOptions> byName) : IOptionsMonitor<ProviderResilienceOptions>
    {
        public ProviderResilienceOptions CurrentValue { get; } = new();

        public ProviderResilienceOptions Get(string? name) => byName.GetValueOrDefault(name ?? string.Empty) ?? CurrentValue;

        public IDisposable? OnChange(Action<ProviderResilienceOptions, string?> listener) => null;
    }

    private sealed class CountingInvoiceProvider(IInvoiceProviderClient inner) : IInvoiceProviderClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IssuedInvoice> IssueAsync(InvoiceRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return inner.IssueAsync(request, cancellationToken);
        }
    }

    private sealed class CountingEmailSender(IEmailSender inner) : IEmailSender
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return inner.SendAsync(message, cancellationToken);
        }
    }
}
