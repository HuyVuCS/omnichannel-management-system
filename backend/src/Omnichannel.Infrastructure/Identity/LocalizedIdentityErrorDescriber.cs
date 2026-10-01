using System.Globalization;
using Microsoft.AspNetCore.Identity;

namespace Omnichannel.Infrastructure.Identity;

/// <summary>
/// Password-policy messages in the language of the request (<c>Accept-Language: vi</c>; English otherwise). They are
/// shown next to the password field, like FluentValidation's localised messages. Error <b>codes</b> are unchanged, and
/// other Identity errors keep their English text: the API maps them to stable error codes instead of showing them.
/// </summary>
internal sealed class LocalizedIdentityErrorDescriber : IdentityErrorDescriber
{
    private static bool IsVietnamese => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi";

    public override IdentityError PasswordTooShort(int length) =>
        IsVietnamese
            ? Error(nameof(PasswordTooShort), $"Mật khẩu phải có ít nhất {length} ký tự.")
            : base.PasswordTooShort(length);

    public override IdentityError PasswordRequiresDigit() =>
        IsVietnamese
            ? Error(nameof(PasswordRequiresDigit), "Mật khẩu phải có ít nhất một chữ số ('0'-'9').")
            : base.PasswordRequiresDigit();

    public override IdentityError PasswordRequiresLower() =>
        IsVietnamese
            ? Error(nameof(PasswordRequiresLower), "Mật khẩu phải có ít nhất một chữ thường ('a'-'z').")
            : base.PasswordRequiresLower();

    public override IdentityError PasswordRequiresUpper() =>
        IsVietnamese
            ? Error(nameof(PasswordRequiresUpper), "Mật khẩu phải có ít nhất một chữ hoa ('A'-'Z').")
            : base.PasswordRequiresUpper();

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        IsVietnamese
            ? Error(nameof(PasswordRequiresNonAlphanumeric), "Mật khẩu phải có ít nhất một ký tự không phải chữ hoặc số.")
            : base.PasswordRequiresNonAlphanumeric();

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        IsVietnamese
            ? Error(nameof(PasswordRequiresUniqueChars), $"Mật khẩu phải có ít nhất {uniqueChars} ký tự khác nhau.")
            : base.PasswordRequiresUniqueChars(uniqueChars);

    private static IdentityError Error(string code, string description) => new() { Code = code, Description = description };
}
