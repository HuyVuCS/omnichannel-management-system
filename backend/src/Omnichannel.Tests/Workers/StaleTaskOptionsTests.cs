using Microsoft.Extensions.Options;
using Omnichannel.Application.Processing;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.OutboxPublisher;
using Omnichannel.Workers.StaleTaskWorker;

namespace Omnichannel.Tests.Workers;

/// <summary>Task 026b Part C: the settings of the stale-task sweep and what they are checked against.</summary>
public sealed class StaleTaskOptionsTests
{
    private static readonly TimeSpan DefaultLongestRetryDelay = RabbitMqTopology.DefaultRetryDelays.Max();

    private static IReadOnlyList<string> Validate(StaleTaskOptions options, TimeSpan? retryDelay = null, TimeSpan? publisherWait = null) =>
        StaleTaskOptionsRules.Validate(
            options,
            retryDelay ?? DefaultLongestRetryDelay,
            publisherWait ?? StaleTaskOptionsValidator.LongestWaitOf(new OutboxPublisherOptions()));

    [Fact]
    public void The_defaults_are_valid_against_the_default_retry_delays_and_publisher_backoff()
    {
        var options = new StaleTaskOptions();

        Assert.Equal(
            (true, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), 200),
            (options.Enabled, options.Interval, options.StaleAfter, options.MaxPerRun));
        Assert.Empty(Validate(options));
    }

    [Fact]
    public void The_publishers_longest_wait_is_the_sum_of_its_backoffs_before_the_last_attempt()
    {
        // Defaults: 10 attempts, 1 s doubling up to 5 min - nine waits of 1 + 2 + 4 + ... + 256 s.
        Assert.Equal(TimeSpan.FromSeconds(511), StaleTaskOptionsValidator.LongestWaitOf(new OutboxPublisherOptions()));
        // Capped: 1 + 2 + 3 + 3 s.
        Assert.Equal(
            TimeSpan.FromSeconds(9),
            StaleTaskOptionsValidator.LongestWaitOf(new OutboxPublisherOptions { MaxAttempts = 5, MaxBackoff = TimeSpan.FromSeconds(3) }));
        // One attempt: no wait at all.
        Assert.Equal(TimeSpan.Zero, StaleTaskOptionsValidator.LongestWaitOf(new OutboxPublisherOptions { MaxAttempts = 1 }));
    }

    [Theory]
    // The longest retry delay decides (10 min): twice that.
    [InlineData(600, 511, 1200, true)]
    [InlineData(600, 511, 1199, false)]
    // The publisher decides.
    [InlineData(4, 13, 26, true)]
    [InlineData(4, 13, 25, false)]
    public void StaleAfter_must_be_twice_the_longest_wait_of_a_healthy_task(int retryDelay, int publisherWait, int staleAfter, bool valid)
    {
        var failures = Validate(
            new StaleTaskOptions { StaleAfter = TimeSpan.FromSeconds(staleAfter) },
            TimeSpan.FromSeconds(retryDelay),
            TimeSpan.FromSeconds(publisherWait));

        if (valid)
        {
            Assert.Empty(failures);
        }
        else
        {
            var failure = Assert.Single(failures);
            Assert.StartsWith("StaleTasks:StaleAfter", failure);
            // It names what it was measured against.
            Assert.Contains("RabbitMq:RetryDelays", failure);
            Assert.Contains("OutboxPublisher", failure);
        }
    }

    [Fact]
    public void A_sweep_that_is_switched_off_is_checked_like_one_that_is_on()
    {
        var failures = Validate(new StaleTaskOptions { Enabled = false, StaleAfter = TimeSpan.FromMinutes(1) });

        Assert.StartsWith("StaleTasks:StaleAfter", Assert.Single(failures));
    }

    [Theory]
    [InlineData(0, 200, "StaleTasks:Interval")]
    [InlineData(86401, 200, "StaleTasks:Interval")]
    [InlineData(300, 0, "StaleTasks:MaxPerRun")]
    [InlineData(300, 1001, "StaleTasks:MaxPerRun")]
    public void Interval_and_MaxPerRun_have_ranges(int intervalSeconds, int maxPerRun, string named)
    {
        var failures = Validate(new StaleTaskOptions { Interval = TimeSpan.FromSeconds(intervalSeconds), MaxPerRun = maxPerRun });

        Assert.StartsWith(named, Assert.Single(failures));
    }

    [Fact]
    public void The_validator_of_the_host_reads_the_configured_retry_delays_and_publisher_settings()
    {
        var validator = new StaleTaskOptionsValidator(
            Options.Create(new RabbitMqOptions { RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(20)] }),
            Options.Create(new OutboxPublisherOptions()));

        // 2 x 20 min: the default of 30 min is too short for this host.
        Assert.True(validator.Validate(null, new StaleTaskOptions()).Failed);
        Assert.True(validator.Validate(null, new StaleTaskOptions { StaleAfter = TimeSpan.FromMinutes(40) }).Succeeded);
    }
}
