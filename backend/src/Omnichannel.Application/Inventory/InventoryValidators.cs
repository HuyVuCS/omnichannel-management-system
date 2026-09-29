using FluentValidation;
using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Inventory;

public sealed class InventoryListQueryValidator() : PageQueryValidator<InventoryListQuery>(InventoryListQuery.SortFields);

public sealed class ImportInventoryRequestValidator : AbstractValidator<ImportInventoryRequest>
{
    public const int MaxDocumentNumberLength = 64;
    public const int MaxDocumentSeriesLength = 32;
    public const int MaxItems = 200;
    public const int MaxStockPerItem = 1_000_000;

    public ImportInventoryRequestValidator()
    {
        RuleFor(x => x.DocumentNumber).NotEmpty().MaximumLength(MaxDocumentNumberLength);
        RuleFor(x => x.DocumentSeries).MaximumLength(MaxDocumentSeriesLength);

        RuleFor(x => x.Items)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(items => items.Count <= MaxItems)
            .WithMessage($"'{{PropertyName}}' must contain at most {MaxItems} items.")
            .Must(items => items.All(i => i is not null))
            .WithMessage("'{PropertyName}' must not contain null items.")
            .Must(items => items.Select(i => i.ProductId).Distinct().Count() == items.Count)
            .WithMessage("'{PropertyName}' must not contain the same product more than once.");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty();
            item.RuleFor(i => i.Stock).InclusiveBetween(1, MaxStockPerItem);
        });
    }
}

public sealed class InventoryMovementListQueryValidator : PageQueryValidator<InventoryMovementListQuery>
{
    public const int MaxReferenceIdLength = 128;

    public InventoryMovementListQueryValidator()
        : base(InventoryMovementListQuery.SortFields)
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.ReferenceType).IsInEnum();
        RuleFor(x => x.ReferenceId).MaximumLength(MaxReferenceIdLength);
        RuleFor(x => x.To)
            .GreaterThanOrEqualTo(x => x.From)
            .When(x => x.From.HasValue && x.To.HasValue)
            .WithMessage("'To' must not be earlier than 'From'.");
    }
}
