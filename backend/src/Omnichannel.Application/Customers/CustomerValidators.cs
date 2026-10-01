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
        RuleFor(x => x.Name).NotEmpty().MaximumLength(MaxNameLength);

        RuleFor(x => x.Phone)
            .MaximumLength(MaxPhoneLength)
            .Matches(PhonePattern)
            .WithMessage(_ => ValidationMessages.Get(ValidationMessages.Keys.PhoneCharacters))
            .When(x => !string.IsNullOrWhiteSpace(x.Phone));

        RuleFor(x => x.Email)
            .MaximumLength(MaxEmailLength)
            .EmailAddress()
            .When(x => !string.IsNullOrWhiteSpace(x.Email));

        RuleFor(x => x.Address).MaximumLength(MaxAddressLength);
    }
}

public sealed class CustomerListQueryValidator() : PageQueryValidator<CustomerListQuery>(CustomerListQuery.SortFields);
