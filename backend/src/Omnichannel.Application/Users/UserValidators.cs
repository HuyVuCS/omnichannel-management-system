using FluentValidation;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Application.Users;

internal static class UserRules
{
    /// <summary>Resolved per request (UI culture). <c>{PropertyName}</c> is left for FluentValidation.</summary>
    public static string RoleMessage() =>
        ValidationMessages.Get(ValidationMessages.Keys.RoleUnknown, ("Roles", string.Join(", ", AppRoles.All)));

    public static bool IsKnownRole(string? role) => AppRoles.TryNormalize(role, out _);
}

/// <summary>
/// Shape only: the password policy (length, character classes) is enforced by ASP.NET Core Identity when the user is
/// stored, so there is one definition of it.
/// </summary>
public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .MaximumLength(LoginRequestValidator.MaxEmailLength)
            .EmailAddress();

        RuleFor(x => x.Password)
            .NotEmpty()
            .MaximumLength(LoginRequestValidator.MaxPasswordLength);

        RuleFor(x => x.Role)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(UserRules.IsKnownRole)
            .WithMessage(_ => UserRules.RoleMessage());
    }
}

public sealed class ChangeUserRoleRequestValidator : AbstractValidator<ChangeUserRoleRequest>
{
    public ChangeUserRoleRequestValidator()
    {
        RuleFor(x => x.Role)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(UserRules.IsKnownRole)
            .WithMessage(_ => UserRules.RoleMessage());
    }
}

public sealed class ResetUserPasswordRequestValidator : AbstractValidator<ResetUserPasswordRequest>
{
    public ResetUserPasswordRequestValidator()
    {
        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MaximumLength(LoginRequestValidator.MaxPasswordLength);
    }
}

public sealed class UserListQueryValidator : PageQueryValidator<UserListQuery>
{
    public UserListQueryValidator()
        : base(UserListQuery.SortFields)
    {
        RuleFor(x => x.Role)
            .Must(UserRules.IsKnownRole)
            .When(x => !string.IsNullOrWhiteSpace(x.Role))
            .WithMessage(_ => UserRules.RoleMessage());
    }
}
