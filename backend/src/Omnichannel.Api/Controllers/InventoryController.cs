using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Inventory;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/inventory")]
[Authorize(Policy = AuthorizationPolicies.AnyStaff)]
public sealed class InventoryController(
    IInventoryQueryService inventoryQueryService,
    IInventoryImportService inventoryImportService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<InventoryItemResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<InventoryItemResponse>>> List(
        [FromQuery] InventoryListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await inventoryQueryService.ListAsync(query, cancellationToken));

    [HttpPost("import")]
    [Authorize(Policy = AuthorizationPolicies.ManagerOrAdmin)]
    [ProducesResponseType<ImportInventoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ImportInventoryResponse>> Import(
        ImportInventoryRequest request,
        CancellationToken cancellationToken) =>
        Ok(await inventoryImportService.ImportAsync(request, cancellationToken));

    [HttpGet("movements")]
    [ProducesResponseType<PagedResult<InventoryMovementResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<InventoryMovementResponse>>> ListMovements(
        [FromQuery] InventoryMovementListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await inventoryQueryService.ListMovementsAsync(query, cancellationToken));
}
