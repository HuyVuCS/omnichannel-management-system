using Omnichannel.Infrastructure.RabbitMQ;
using RabbitMQ.Client;

namespace Omnichannel.Tests.Workers;

public sealed class RetryTopologyTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 30)]
    [InlineData(3, 120)]
    [InlineData(4, 600)]
    [InlineData(5, 600)]
    [InlineData(99, 600)]
    public void Tier_follows_the_attempt_number_and_stays_at_the_longest(int attemptNo, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RabbitMqTopology.RetryDelayFor(attemptNo, RabbitMqTopology.DefaultRetryDelays));
    }

    [Fact]
    public void Tier_selection_rejects_invalid_input()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RabbitMqTopology.RetryDelayFor(0, RabbitMqTopology.DefaultRetryDelays));
        Assert.Throws<ArgumentOutOfRangeException>(() => RabbitMqTopology.RetryDelayFor(1, []));
    }

    [Theory]
    [InlineData(5_000, "omnichannel.delay.5s")]
    [InlineData(30_000, "omnichannel.delay.30s")]
    [InlineData(120_000, "omnichannel.delay.2m")]
    [InlineData(600_000, "omnichannel.delay.10m")]
    [InlineData(90_000, "omnichannel.delay.90s")]
    [InlineData(1_500, "omnichannel.delay.1500ms")]
    public void Delay_names_encode_the_ttl(int milliseconds, string expected)
    {
        Assert.Equal(expected, RabbitMqTopology.DelayNameOf(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void Delay_queue_dead_letters_to_the_retry_exchange_keeping_the_routing_key()
    {
        var arguments = RabbitMqTopology.DelayQueueArguments(TimeSpan.FromSeconds(30));

        Assert.Equal("quorum", arguments["x-queue-type"]);
        Assert.Equal(30_000, arguments["x-message-ttl"]);
        Assert.Equal(RabbitMqTopology.RetryExchange, arguments["x-dead-letter-exchange"]);
        Assert.False(arguments.ContainsKey("x-dead-letter-routing-key"));
        Assert.Equal("at-least-once", arguments["x-dead-letter-strategy"]);
        Assert.Equal("reject-publish", arguments["x-overflow"]);
    }

    [Fact]
    public void Retry_copy_keeps_identity_and_replaces_broker_headers()
    {
        var original = new BasicProperties
        {
            MessageId = "m-1",
            CorrelationId = "c-1",
            Type = "order.created",
            ContentType = "application/json",
            Timestamp = new AmqpTimestamp(1_700_000_000),
            Headers = new Dictionary<string, object?>
            {
                ["x-death"] = new List<object>(),
                ["x-delivery-count"] = 3L,
                ["x-dotnet-pub-seq-no"] = 7L,
                ["x-retry-attempt"] = 1,
                ["tenant"] = "kept"
            }
        };

        var copy = RabbitMqRetryPublisher.CreateRetryProperties(original, new Dictionary<string, object?>
        {
            [RabbitMqRetryPublisher.RetryAttemptHeader] = 2,
            [RabbitMqRetryPublisher.LastErrorCodeHeader] = "timeout"
        });

        Assert.Equal(("m-1", "c-1", "order.created", "application/json"), (copy.MessageId, copy.CorrelationId, copy.Type, copy.ContentType));
        Assert.Equal(DeliveryModes.Persistent, copy.DeliveryMode);
        Assert.Equal(1_700_000_000, copy.Timestamp.UnixTime);
        Assert.Equal(
            new[] { "tenant", "x-last-error-code", "x-retry-attempt" },
            copy.Headers!.Keys.Order());
        Assert.Equal(2, copy.Headers[RabbitMqRetryPublisher.RetryAttemptHeader]);
        Assert.Equal("timeout", copy.Headers[RabbitMqRetryPublisher.LastErrorCodeHeader]);
    }

    [Fact]
    public void Retry_policy_limits()
    {
        var policy = new RetryPolicy(MaxAttempts: 3, DeliveryLimit: 10);

        Assert.True(policy.CanRetry(2));
        Assert.False(policy.CanRetry(3));
        Assert.False(policy.IsLastAllowedDelivery(9));
        Assert.True(policy.IsLastAllowedDelivery(10));
    }

    [Fact]
    public void Retry_delays_must_be_ascending_and_sane()
    {
        var validator = new RabbitMqOptionsValidator();
        RabbitMqOptions Options(params int[] seconds) => new()
        {
            UserName = "u",
            Password = "p",
            RetryDelays = seconds.Select(s => TimeSpan.FromSeconds(s)).ToArray()
        };

        Assert.True(validator.Validate(null, Options()).Succeeded); // defaults
        Assert.True(validator.Validate(null, Options(1, 2, 3, 4)).Succeeded);
        Assert.False(validator.Validate(null, Options(30, 5)).Succeeded);
        Assert.False(validator.Validate(null, Options(0)).Succeeded);
    }
}
