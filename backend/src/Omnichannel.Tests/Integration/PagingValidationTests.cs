using System.Net;
using Omnichannel.Application.Auth;

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
    public async Task Invalid_paging_parameters_return_validation_problem(string url)
    {
        var response = await _staff.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("validation_failed", await response.ReadErrorCodeAsync());
    }
}
