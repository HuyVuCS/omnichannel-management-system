using FluentValidation;

namespace Omnichannel.Application.Orders;

public sealed class SubmitOrderRequestValidator : AbstractValidator<SubmitOrderRequest>
{
    public const int MaxExternalOrderIdLength = 128;
    public const int MaxItems = 100;
    public const int MaxStockPerItem = 10_000;

    public SubmitOrderRequestValidator()
    {
        RuleFor(x => x.ExternalOrderId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(id => id.Trim().Length <= MaxExternalOrderIdLength)
            .WithMessage($"'{{PropertyName}}' must be at most {MaxExternalOrderIdLength} characters.");

        RuleFor(x => x.CustomerId).NotEmpty();

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
