using Omnichannel.Application.Common.Paging;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Channels;

/// <summary>A product mapped on a channel: the central product and how the channel identifies it.</summary>
public sealed record ChannelProductResponse(
    Guid ChannelProductId,
    Guid ChannelId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string? ExternalProductId,
    string? ExternalSku,
    ChannelProductStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>Body of a created or updated mapping.</summary>
/// <param name="SyncTaskId">
/// The stock-sync task that pushes the product's current stock to the channel (<c>GET /api/processing/tasks/{id}</c>);
/// null when no sync was requested (mapping or channel inactive, channel without stock sync, nothing relevant changed).
/// </param>
public sealed record ChannelProductSavedResponse(
    Guid ChannelProductId,
    Guid ChannelId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string? ExternalProductId,
    string? ExternalSku,
    ChannelProductStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    Guid? SyncTaskId);

/// <summary>At least one of <see cref="ExternalProductId"/> and <see cref="ExternalSku"/> is required.</summary>
public sealed record CreateChannelProductRequest(Guid ProductId, string? ExternalProductId, string? ExternalSku);

/// <summary>Channel and product of a mapping cannot change; there is no delete (deactivate instead).</summary>
public sealed record UpdateChannelProductRequest(string? ExternalProductId, string? ExternalSku, ChannelProductStatus Status);

/// <summary>Search matches SKU, product name, external product id or external SKU.</summary>
public sealed record ChannelProductListQuery : PageQuery
{
    public const string SortBySku = "sku";
    public const string SortByCreatedAt = "createdAt";

    public static readonly IReadOnlyList<string> SortFields = [SortBySku, SortByCreatedAt];
}

public static class ChannelProductErrorCodes
{
    /// <summary>The product is already mapped on this channel.</summary>
    public const string MappingExists = "channel_product_exists";

    /// <summary>The external product id belongs to another product on this channel.</summary>
    public const string ExternalProductIdTaken = "external_product_id_taken";

    /// <summary>No such mapping on this channel.</summary>
    public const string MappingNotFound = "channel_product_not_found";
}
