using Omnichannel.Application.Common.Paging;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Inventory;

/// <param name="CreatedBy">Who made the movement: a user id, or <c>system:{worker}</c> for background processing.</param>
/// <param name="CreatedByEmail">
/// E-mail of the user behind <paramref name="CreatedBy"/>; null for system actors and for ids that match no user.
/// </param>
public sealed record InventoryMovementResponse(
    Guid InventoryMovementId,
    Guid ProductId,
    string Sku,
    string ProductName,
    InventoryMovementType Type,
    int Stock,
    InventoryReferenceType ReferenceType,
    string? ReferenceId,
    string? DocumentNumber,
    string? DocumentSeries,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    string? CreatedByEmail);

/// <summary>
/// Always ordered newest first (<c>CreatedAt desc, InventoryMovementId desc</c>): <c>sortBy</c> is not supported and
/// <c>search</c>/<c>sortDir</c> are ignored. <see cref="From"/>/<see cref="To"/> filter on <c>CreatedAt</c> (inclusive).
/// </summary>
public sealed record InventoryMovementListQuery : PageQuery
{
    public static readonly IReadOnlyList<string> SortFields = [];

    public Guid? ProductId { get; init; }
    public InventoryMovementType? Type { get; init; }
    public InventoryReferenceType? ReferenceType { get; init; }
    public string? ReferenceId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}
