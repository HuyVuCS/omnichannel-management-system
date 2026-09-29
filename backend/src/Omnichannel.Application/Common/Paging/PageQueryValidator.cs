using FluentValidation;

namespace Omnichannel.Application.Common.Paging;

/// <summary>Base rules for every paged query; derived validators pass their sortable fields.</summary>
public abstract class PageQueryValidator<TQuery> : AbstractValidator<TQuery>
    where TQuery : PageQuery
{
    protected PageQueryValidator(IReadOnlyCollection<string> sortFields)
    {
        RuleFor(x => x.Page).InclusiveBetween(1, PageQuery.MaxPage);

        RuleFor(x => x.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);

        RuleFor(x => x.Search).MaximumLength(PageQuery.MaxSearchLength);

        RuleFor(x => x.SortBy)
            .Must(sortBy => sortFields.Contains(sortBy!, StringComparer.OrdinalIgnoreCase))
            .When(x => x.SortBy is not null)
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", sortFields)}.");

        RuleFor(x => x.SortDir)
            .Must(sortDir => string.Equals(sortDir, PageQuery.Ascending, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sortDir, PageQuery.Descending, StringComparison.OrdinalIgnoreCase))
            .When(x => x.SortDir is not null)
            .WithMessage($"'{{PropertyName}}' must be '{PageQuery.Ascending}' or '{PageQuery.Descending}'.");
    }
}
