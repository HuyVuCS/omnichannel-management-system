namespace Omnichannel.Tests.Integration;

/// <summary>Polling for asynchronous outcomes (background workers) with a hard timeout.</summary>
internal static class Eventually
{
    public static async Task<T> WaitAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> done,
        TimeSpan timeout,
        string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var value = await probe();
            if (done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for: {description}. Last value: {value}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }
}
