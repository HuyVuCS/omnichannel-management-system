using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Customers;

public sealed record CustomerResponse(
    Guid CustomerId,
    string Name,
    string? Phone,
    string? Email,
    string? Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>Body of both POST and PUT /api/customers.</summary>
public sealed record CustomerRequest(string Name, string? Phone, string? Email, string? Address);

/// <summary>Search matches name, phone or email.</summary>
public sealed record CustomerListQuery : PageQuery
{
    public const string SortByName = "name";
    public const string SortByCreatedAt = "createdAt";

    public static readonly IReadOnlyList<string> SortFields = [SortByName, SortByCreatedAt];
}
