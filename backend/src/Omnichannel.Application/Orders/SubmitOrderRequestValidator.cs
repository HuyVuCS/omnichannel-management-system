using FluentValidation;
using Omnichannel.Application.Common.Validation;

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
            .WithMessage(_ => ValidationMessages.Get(
                ValidationMessages.Keys.ExternalOrderIdTooLong,
                ("MaxLength", MaxExternalOrderIdLength)));

        RuleFor(x => x.CustomerId).NotEmpty();

        RuleFor(x => x.Items)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(items => items.Count <= MaxItems)
            .WithMessage(_ => ValidationMessages.Get(ValidationMessages.Keys.ItemsTooMany, ("MaxItems", MaxItems)))
            .Must(items => items.All(i => i is not null))
            .WithMessage(_ => ValidationMessages.Get(ValidationMessages.Keys.ItemsContainNull))
            .Must(items => items.Select(i => i.ProductId).Distinct().Count() == items.Count)
            .WithMessage(_ => ValidationMessages.Get(ValidationMessages.Keys.ItemsDuplicateProduct));

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty();
            item.RuleFor(i => i.Stock).InclusiveBetween(1, MaxStockPerItem);
        });
    }
}
