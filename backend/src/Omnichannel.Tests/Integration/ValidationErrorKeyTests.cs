using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Omnichannel.Application.Auth;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 012 Part A1 (decided in review 011): the keys of a 400 are camelCase JSON paths for body fields, query-string
/// parameters and model-binding errors alike. Compared exactly (ordinal), never case-insensitively.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ValidationErrorKeyTests(ApiFactory factory)
{
    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);

    [Theory]
    // A validator error on a supplied query parameter (model binding registered it as "To" / "SortBy").
    [InlineData("/api/orders?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z", "to")]
    [InlineData("/api/inventory/movements?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z", "to")]
    [InlineData("/api/processing/tasks?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z", "to")]
    [InlineData("/api/customers?sortBy=secret", "sortBy")]
    [InlineData("/api/customers?SORTBY=secret", "sortBy")] // whatever casing the client used
    [InlineData("/api/inventory?sortDir=sideways", "sortDir")]
    // Model-binding errors: no validator ran.
    [InlineData("/api/products?pageSize=abc", "pageSize")]
    [InlineData("/api/customers?page=abc", "page")]
    [InlineData("/api/inventory/movements?productId=not-a-guid", "productId")]
    [InlineData("/api/inventory/movements?referenceType=Nothing", "referenceType")]
    public async Task Query_string_errors_use_the_camel_case_parameter_name(string url, string expectedKey)
    {
        var response = await _manager.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([expectedKey], await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task Body_errors_are_still_json_paths()
    {
        var response = await _manager.PostAsJsonAsync("/api/inventory/import", new
        {
            documentNumber = "",
            items = new[] { new { productId = Guid.NewGuid(), stock = 0 } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["documentNumber", "items[0].stock"], await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task Malformed_json_is_reported_under_the_field_where_the_parser_stopped()
    {
        // Since task 026 (B3) the parser's own path ($.sku) is not passed on: the key is the field's, like any other.
        using var content = new StringContent("{ \"sku\": ", Encoding.UTF8, "application/json");

        var response = await _manager.PostAsync("/api/products", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["sku"], await ErrorKeysAsync(response));
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", document.RootElement.GetProperty("errorCode").GetString());
        return document.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
    }
}
