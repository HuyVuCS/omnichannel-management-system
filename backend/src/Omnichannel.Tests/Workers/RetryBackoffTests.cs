using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Tests.Workers;

public sealed class RetryBackoffTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(9, 256)]
    public void Doubles_per_attempt_within_equal_jitter_bounds(int attempt, int baseSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(baseSeconds / 2d), RetryBackoff.Compute(attempt, Initial, Max, jitter: 0));
        Assert.Equal(TimeSpan.FromSeconds(baseSeconds * 0.75), RetryBackoff.Compute(attempt, Initial, Max, jitter: 0.5));

        var upper = RetryBackoff.Compute(attempt, Initial, Max, jitter: 0.999999);
        Assert.InRange(upper, TimeSpan.FromSeconds(baseSeconds * 0.999), TimeSpan.FromSeconds(baseSeconds));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(31)]
    [InlineData(int.MaxValue)]
    public void Is_capped_at_the_maximum(int attempt)
    {
        Assert.Equal(TimeSpan.FromMinutes(2.5), RetryBackoff.Compute(attempt, Initial, Max, jitter: 0));
        Assert.InRange(RetryBackoff.Compute(attempt, Initial, Max, jitter: 0.999999), TimeSpan.FromMinutes(2.5), Max);
    }

    [Fact]
    public void Random_jitter_stays_in_range()
    {
        for (var i = 0; i < 1000; i++)
        {
            var delay = RetryBackoff.Compute(4, Initial, Max, Random.Shared.NextDouble());
            Assert.InRange(delay, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));
        }
    }

    [Theory]
    [InlineData(0, 0.5)]
    [InlineData(1, -0.1)]
    [InlineData(1, 1.0)]
    public void Rejects_invalid_arguments(int attempt, double jitter)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryBackoff.Compute(attempt, Initial, Max, jitter));
    }
}
