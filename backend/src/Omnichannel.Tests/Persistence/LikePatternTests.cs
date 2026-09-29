using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Tests.Persistence;

public sealed class LikePatternTests
{
    [Theory]
    [InlineData("milk", "%milk%")]
    [InlineData("50%", @"%50\%%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData(@"c:\x", @"%c:\\x%")]
    [InlineData(@"\%", @"%\\\%%")]
    public void Escapes_wildcards_and_escape_character(string input, string expected)
    {
        Assert.Equal(expected, LikePattern.Contains(input));
    }
}
