using Microsoft.AspNetCore.Identity;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Infrastructure.Identity;

/// <summary>
/// Password-policy messages from the shared validation resources (<see cref="ValidationMessages"/>), so they follow the
/// language of the request like every other validation text; the English texts are Identity's own. Error <b>codes</b> are
/// unchanged, and other Identity errors keep their built-in text: the API maps them to stable error codes instead of
/// showing them.
/// </summary>
internal sealed class LocalizedIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError PasswordTooShort(int length) =>
        Error(nameof(PasswordTooShort), ValidationMessages.Get(ValidationMessages.Keys.PasswordTooShort, ("Length", length)));

    public override IdentityError PasswordRequiresDigit() =>
        Error(nameof(PasswordRequiresDigit), ValidationMessages.Get(ValidationMessages.Keys.PasswordRequiresDigit));

    public override IdentityError PasswordRequiresLower() =>
        Error(nameof(PasswordRequiresLower), ValidationMessages.Get(ValidationMessages.Keys.PasswordRequiresLower));

    public override IdentityError PasswordRequiresUpper() =>
        Error(nameof(PasswordRequiresUpper), ValidationMessages.Get(ValidationMessages.Keys.PasswordRequiresUpper));

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        Error(
            nameof(PasswordRequiresNonAlphanumeric),
            ValidationMessages.Get(ValidationMessages.Keys.PasswordRequiresNonAlphanumeric));

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        Error(
            nameof(PasswordRequiresUniqueChars),
            ValidationMessages.Get(ValidationMessages.Keys.PasswordRequiresUniqueChars, ("UniqueChars", uniqueChars)));

    private static IdentityError Error(string code, string description) => new() { Code = code, Description = description };
}
