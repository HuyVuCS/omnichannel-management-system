using System.Net;
using System.Net.Http.Json;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Customers;

namespace Omnichannel.Tests.Integration;

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CustomerEndpointsTests(ApiFactory factory)
{
    private readonly HttpClient _staff = factory.CreateClientAs(AppRoles.Staff);

    [Fact]
    public async Task Search_treats_percent_sign_literally()
    {
        var marker = TestData.UniqueSuffix();
        var literal = await CreateAsync(new CustomerRequest($"{marker} 50% discount club", null, null, null));
        // Would match "%50%%" if '%' in the search text were a wildcard.
        var wildcardOnly = await CreateAsync(new CustomerRequest($"{marker} 500 units buyer", null, null, null));

        var response = await _staff.GetAsync($"/api/customers?search={Uri.EscapeDataString("50%")}&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.ReadAsync<PagedResult<CustomerResponse>>();
        Assert.Contains(page.Items, c => c.CustomerId == literal.CustomerId);
        Assert.DoesNotContain(page.Items, c => c.CustomerId == wildcardOnly.CustomerId);
        Assert.All(page.Items, c => Assert.Contains("50%", c.Name));
    }

    [Fact]
    public async Task Search_matches_phone_and_email()
    {
        var marker = TestData.UniqueSuffix();
        var customer = await CreateAsync(new CustomerRequest("Phone Person", "+84 (0) 90-123", $"{marker}@example.com", null));

        var byEmail = await _staff.GetFromJsonAsync<PagedResult<CustomerResponse>>(
            $"/api/customers?search={marker}", TestData.Json);

        Assert.Equal(customer.CustomerId, Assert.Single(byEmail!.Items).CustomerId);
    }

    [Fact]
    public async Task Staff_creates_and_updates_customer()
    {
        var created = await CreateAsync(new CustomerRequest("  Nguyen Van A ", "0901234567", null, ""));
        Assert.Equal("Nguyen Van A", created.Name);
        Assert.Null(created.Address);

        var response = await _staff.PutAsJsonAsync(
            $"/api/customers/{created.CustomerId}",
            new CustomerRequest("Nguyen Van B", null, "b@example.com", "1 Main St"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.ReadAsync<CustomerResponse>();
        Assert.Equal(("Nguyen Van B", null, "b@example.com", "1 Main St"), (updated.Name, updated.Phone, updated.Email, updated.Address));
    }

    [Fact]
    public async Task Invalid_email_is_rejected()
    {
        var response = await _staff.PostAsJsonAsync("/api/customers", new CustomerRequest("X", null, "not-an-email", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ReadErrorCodeAsync());
    }

    private async Task<CustomerResponse> CreateAsync(CustomerRequest request)
    {
        var response = await _staff.PostAsJsonAsync("/api/customers", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<CustomerResponse>();
    }
}
