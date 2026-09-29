namespace Omnichannel.Application.Common.Paging;

/// <summary>
/// Query-string contract shared by paged list endpoints:
/// <c>?page=1&amp;pageSize=20&amp;search=...&amp;sortBy=...&amp;sortDir=asc|desc</c>.
/// Each endpoint derives its own query type and whitelists <see cref="SortBy"/> in its validator.
/// </summary>
public abstract record PageQuery
{
    /// <summary>Upper bound for <see cref="Page"/>; keeps <see cref="Skip"/> far from int overflow.</summary>
    public const int MaxPage = 10_000;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxSearchLength = 100;
    public const string Ascending = "asc";
    public const string Descending = "desc";

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = DefaultPageSize;
    public string? Search { get; init; }
    public string? SortBy { get; init; }
    public string? SortDir { get; init; }

    public bool IsDescending => string.Equals(SortDir, Descending, StringComparison.OrdinalIgnoreCase);

    public int Skip => (Page - 1) * PageSize;

    /// <summary>Search text trimmed; null when blank.</summary>
    public string? SearchTerm => string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();

    /// <summary>True when <see cref="SortBy"/> names <paramref name="field"/> (case-insensitive).</summary>
    public bool IsSortedBy(string field) => string.Equals(SortBy, field, StringComparison.OrdinalIgnoreCase);
}
