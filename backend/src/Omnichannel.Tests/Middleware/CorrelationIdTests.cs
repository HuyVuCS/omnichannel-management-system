using Omnichannel.Api.Middleware;

namespace Omnichannel.Tests.Middleware;

public sealed class CorrelationIdTests
{
    [Theory]
    [InlineData("3f2c9a1e-7b1d-4c55-9f0a-2b8e6d1c4a77")]
    [InlineData("order_1001.retry-2")]
    public void Accepts_well_formed_client_ids(string value)
    {
        Assert.Equal(value, CorrelationIdMiddleware.Resolve(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc\r\nInjected: header")]
    [InlineData("<script>")]
    public void Replaces_missing_or_unsafe_ids(string? value)
    {
        var resolved = CorrelationIdMiddleware.Resolve(value);

        Assert.NotEqual(value, resolved);
        Assert.True(CorrelationIdMiddleware.IsValid(resolved));
    }

    [Fact]
    public void Rejects_ids_longer_than_limit()
    {
        Assert.False(CorrelationIdMiddleware.IsValid(new string('a', CorrelationIdMiddleware.MaxLength + 1)));
    }
}
