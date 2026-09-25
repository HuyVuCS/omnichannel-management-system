using FluentValidation;

namespace Omnichannel.Application.Auth;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    // Bounded to keep password hashing cost predictable (avoid hashing megabyte-sized inputs).
    public const int MaxPasswordLength = 128;
    public const int MaxEmailLength = 256;

    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .MaximumLength(MaxEmailLength)
            .EmailAddress();

        RuleFor(x => x.Password)
            .NotEmpty()
            .MaximumLength(MaxPasswordLength);
    }
}
