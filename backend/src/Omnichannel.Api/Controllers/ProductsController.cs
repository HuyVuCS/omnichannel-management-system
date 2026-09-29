using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Products;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/products")]
[Authorize(Policy = AuthorizationPolicies.AnyStaff)]
public sealed class ProductsController(IProductService productService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<ProductResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<ProductResponse>>> List(
        [FromQuery] ProductListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await productService.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await productService.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.ManagerOrAdmin)]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductResponse>> Create(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var product = await productService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = product.ProductId }, product);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.ManagerOrAdmin)]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> Update(
        Guid id,
        UpdateProductRequest request,
        CancellationToken cancellationToken) =>
        Ok(await productService.UpdateAsync(id, request, cancellationToken));
}
