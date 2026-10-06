using FluentValidation;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Application.Customers;

public sealed class CustomerRequestValidator : AbstractValidator<CustomerRequest>
{
    public const int MaxNameLength = 200;
    public const int MaxPhoneLength = 32;
    public const int MaxEmailLength = 256;
    public const int MaxAddressLength = 500;
    public const string PhonePattern = @"^[0-9+\-() ]+$";

    public CustomerRequestValidator()
    {
        RuleFor(x => x.Name).CustomerName();
        RuleFor(x => x.Phone).CustomerPhone(x => x.Phone);
        RuleFor(x => x.Email).CustomerEmail(x => x.Email);
        RuleFor(x => x.Address).MaximumLength(MaxAddressLength);
    }
}

public sealed class CustomerListQueryValidator() : PageQueryValidator<CustomerListQuery>(CustomerListQuery.SortFields);

/// <summary>
/// The rules of a customer's name, phone and e-mail, wherever a request carries them (the customer form, the customer of
/// a webhook order). Phone and e-mail are optional: a blank one is not checked (it is stored as null).
/// </summary>
public static class CustomerRules
{
    public static IRuleBuilderOptions<T, string> CustomerName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(CustomerRequestValidator.MaxNameLength);

    /// <param name="phone">The phone of the validated object (the rule applies only when it is not blank).</param>
    public static IRuleBuilderOptions<T, string?> CustomerPhone<T>(this IRuleBuilder<T, string?> rule, Func<T, string?> phone) =>
        rule
            .MaximumLength(CustomerRequestValidator.MaxPhoneLength)
            .Matches(CustomerRequestValidator.PhonePattern)
            .WithMessage(_ => ValidationMessages.Get(ValidationMessages.Keys.PhoneCharacters))
            .When(x => !string.IsNullOrWhiteSpace(phone(x)));

    /// <param name="email">The e-mail of the validated object (the rule applies only when it is not blank).</param>
    public static IRuleBuilderOptions<T, string?> CustomerEmail<T>(this IRuleBuilder<T, string?> rule, Func<T, string?> email) =>
        rule
            .MaximumLength(CustomerRequestValidator.MaxEmailLength)
            .EmailAddress()
            .When(x => !string.IsNullOrWhiteSpace(email(x)));
}
