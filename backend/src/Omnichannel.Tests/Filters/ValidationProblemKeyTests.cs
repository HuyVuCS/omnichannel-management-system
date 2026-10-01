using Omnichannel.Api.Filters;

namespace Omnichannel.Tests.Filters;

/// <summary>Task 012 Part A1: every key of a 400 is a camelCase JSON path, whatever produced the error.</summary>
public sealed class ValidationProblemKeyTests
{
    [Theory]
    [InlineData("To", "to")]
    [InlineData("SortBy", "sortBy")]
    [InlineData("PageSize", "pageSize")]
    [InlineData("Items[0].ProductId", "items[0].productId")]
    [InlineData("Items[12].Lines[3].Stock", "items[12].lines[3].stock")]
    [InlineData("items[0].stock", "items[0].stock")] // already a JSON path
    [InlineData("SKU", "sku")]
    [InlineData("[0].Name", "[0].name")] // a segment that is only an index stays
    [InlineData("", "")]
    [InlineData("$", "$")] // System.Text.Json body paths of malformed JSON
    [InlineData("$.Items[0].Stock", "$.Items[0].Stock")]
    public void Keys_are_camel_cased_per_segment_and_keep_their_indexes(string key, string expected)
    {
        Assert.Equal(expected, ValidationProblemResponse.NormaliseKey(key));
    }

    [Fact]
    public void Keys_that_collapse_into_one_keep_all_their_messages_once()
    {
        var errors = new Dictionary<string, string[]>
        {
            ["To"] = ["binding", "shared"],
            ["to"] = ["shared", "validator"],
            ["$.to"] = ["json"]
        };

        var normalised = ValidationProblemResponse.NormaliseKeys(errors);

        Assert.Equal(["$.to", "to"], normalised.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["binding", "shared", "validator"], normalised["to"]);
        Assert.Equal(["json"], normalised["$.to"]);
    }
}
