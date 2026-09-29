using System.Net;
using System.Text;
using System.Text.Json;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Tests.Integration;

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PagingValidationTests(ApiFactory factory)
{
    private readonly HttpClient _staff = factory.CreateClientAs(AppRoles.Staff);

    [Theory]
    [InlineData("/api/products?pageSize=101")]
    [InlineData("/api/products?sortBy=unknown")]
    [InlineData("/api/customers?pageSize=101")]
    [InlineData("/api/customers?sortBy=unknown")]
    [InlineData("/api/inventory?pageSize=101")]
    [InlineData("/api/inventory?sortBy=unknown")]
    [InlineData("/api/inventory?page=0")]
    [InlineData("/api/inventory?sortDir=sideways")]
    [InlineData("/api/products?page=2147483647")] // would overflow Skip without the page bound
    [InlineData("/api/products?page=10001")]
    [InlineData("/api/products?pageSize=abc")] // model-binding error, not a validator error
    [InlineData("/api/customers?page=abc")]
    public async Task Invalid_paging_parameters_return_validation_problem(string url)
    {
        var response = await _staff.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("validation_failed", await response.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Model_binding_errors_carry_error_code_and_correlation_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products?pageSize=abc");
        request.Headers.Add("X-Correlation-Id", "binding-test-1");

        var response = await _staff.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("binding-test-1", body.RootElement.GetProperty("correlationId").GetString());
        Assert.NotEmpty(body.RootElement.GetProperty("errors").EnumerateObject());
    }

    [Fact]
    public async Task Malformed_json_body_returns_validation_problem()
    {
        var manager = factory.CreateClientAs(AppRoles.Manager);
        using var content = new StringContent("{ \"sku\": ", Encoding.UTF8, "application/json");

        var response = await manager.PostAsync("/api/products", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Last_allowed_page_returns_empty_page()
    {
        var response = await _staff.GetAsync($"/api/products?page={PageQuery.MaxPage}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
