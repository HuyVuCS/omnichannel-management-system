using FluentValidation;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Customers;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Application.Orders;

/// <summary>
/// The limits of the order intake (<see cref="SubmitOrderRequestValidator"/>) for an order in the channel's words, and
/// the customer rules of <see cref="CustomerRequestValidator"/> for the customer it carries.
/// </summary>
public sealed class WebhookOrderRequestValidator : AbstractValidator<WebhookOrderRequest>
{
    public WebhookOrderRequestValidator()
    {
        RuleFor(x => x.ExternalOrderId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .TrimmedMaximumLength(SubmitOrderRequestValidator.MaxExternalOrderIdLength);

        RuleFor(x => x.Customer).NotNull().SetValidator(new WebhookCustomerValidator());

        RuleFor(x => x.Items)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(items => items.Count <= SubmitOrderRequestValidator.MaxItems)
            .WithMessage(_ => ValidationMessages.Get(
                ValidationMessages.Keys.ItemsTooMany,
                ("MaxItems", SubmitOrderRequestValidator.MaxItems)))
            .Must(items => items.All(i => i is not null))
            .WithMessage(_ => ValidationMessages.Get(ValidationMessages.Keys.ItemsContainNull))
            // A line that names no product (or two) has its own error and takes no part here.
            .Must(items => items.Where(i => i.NamesOneProduct()).GroupBy(i => i.Key(), StringComparer.Ordinal).All(g => g.Count() == 1))
            .WithMessage(_ => ValidationMessages.Get(ValidationMessages.Keys.ItemsDuplicateProduct));

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ExternalProductId)
                .Cascade(CascadeMode.Stop)
                .Must((line, _) => line.NamesOneProduct())
                .WithMessage(_ => ValidationMessages.Get(
                    ValidationMessages.Keys.ItemIdentifierExactlyOne,
                    ("ExternalSku", FieldNames.ForMember(nameof(WebhookOrderItem.ExternalSku)))))
                .TrimmedMaximumLength(ChannelProduct.MaxExternalIdLength);
            item.RuleFor(i => i.ExternalSku).TrimmedMaximumLength(ChannelProduct.MaxExternalIdLength);
            item.RuleFor(i => i.Quantity).InclusiveBetween(1, SubmitOrderRequestValidator.MaxStockPerItem);
        });
    }
}

public sealed class WebhookCustomerValidator : AbstractValidator<WebhookCustomer>
{
    public WebhookCustomerValidator()
    {
        RuleFor(x => x.ExternalCustomerId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .TrimmedMaximumLength(ChannelCustomer.MaxExternalCustomerIdLength);

        RuleFor(x => x.Name).CustomerName();
        RuleFor(x => x.Phone).CustomerPhone(x => x.Phone);
        RuleFor(x => x.Email).CustomerEmail(x => x.Email);
    }
}

internal static class WebhookOrderRules
{
    /// <summary>At most <paramref name="maximumLength"/> characters once trimmed (as it is stored); null passes.</summary>
    public static IRuleBuilderOptions<T, string?> TrimmedMaximumLength<T>(this IRuleBuilder<T, string?> rule, int maximumLength) =>
        rule
            .Must(value => value is null || value.Trim().Length <= maximumLength)
            .WithMessage(_ => ValidationMessages.Get(ValidationMessages.Keys.ExternalOrderIdTooLong, ("MaxLength", maximumLength)));
}
