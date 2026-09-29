using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Customers;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/customers")]
[Authorize(Policy = AuthorizationPolicies.AnyStaff)]
public sealed class CustomersController(ICustomerService customerService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<CustomerResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<CustomerResponse>>> List(
        [FromQuery] CustomerListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await customerService.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await customerService.GetAsync(id, cancellationToken));

    [HttpPost]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CustomerResponse>> Create(CustomerRequest request, CancellationToken cancellationToken)
    {
        var customer = await customerService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = customer.CustomerId }, customer);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerResponse>> Update(
        Guid id,
        CustomerRequest request,
        CancellationToken cancellationToken) =>
        Ok(await customerService.UpdateAsync(id, request, cancellationToken));
}
