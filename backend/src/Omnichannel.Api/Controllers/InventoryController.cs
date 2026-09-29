using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Inventory;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/inventory")]
[Authorize(Policy = AuthorizationPolicies.AnyStaff)]
public sealed class InventoryController(IInventoryQueryService inventoryQueryService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<InventoryItemResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<InventoryItemResponse>>> List(
        [FromQuery] InventoryListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await inventoryQueryService.ListAsync(query, cancellationToken));
}
