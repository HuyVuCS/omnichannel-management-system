namespace Omnichannel.Workers.OutboxPublisher;

/// <summary>Exponential backoff with jitter for publish retries.</summary>
public static class OutboxBackoff
{
    /// <summary>
    /// Delay before the next attempt after <paramref name="failedAttempts"/> failures:
    /// base = <paramref name="initial"/> × 2^(failedAttempts − 1), capped at <paramref name="max"/>; the result is
    /// uniformly spread over [base / 2, base] ("equal jitter"), so instances that failed together do not retry together.
    /// </summary>
    /// <param name="jitter">Random value in [0, 1), e.g. <c>Random.Shared.NextDouble()</c>.</param>
    public static TimeSpan Compute(int failedAttempts, TimeSpan initial, TimeSpan max, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failedAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(jitter, 0d);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(jitter, 1d);

        // 2^30 × any positive initial already exceeds every sensible cap; avoids overflow for large attempt counts.
        var exponent = Math.Min(failedAttempts - 1, 30);
        var baseTicks = Math.Min(initial.Ticks * Math.Pow(2, exponent), max.Ticks);

        return TimeSpan.FromTicks((long)(baseTicks / 2 + baseTicks / 2 * jitter));
    }
}
