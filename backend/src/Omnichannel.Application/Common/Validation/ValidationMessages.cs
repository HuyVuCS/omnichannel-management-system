using System.Globalization;
using System.Resources;

namespace Omnichannel.Application.Common.Validation;

/// <summary>
/// The one source of every validation text this application writes itself (custom FluentValidation messages, validation
/// failures raised by services, the Identity password policy). Texts live in <c>Resources/ValidationMessages.resx</c>
/// (English, the default) and <c>ValidationMessages.vi.resx</c> and are read in the UI culture of the request
/// (<c>Accept-Language</c>). FluentValidation's built-in messages are localised by FluentValidation itself.
/// <para>
/// In validators use the lambda overload so the text is resolved per request:
/// <c>.WithMessage(_ =&gt; ValidationMessages.Get(ValidationMessages.Keys.SkuFormat, ("MaxLength", 64)))</c>.
/// <c>{PropertyName}</c> is filled in by FluentValidation; every other <c>{Name}</c> by the arguments given here.
/// </para>
/// Error keys and <c>errorCode</c> values are never translated.
/// </summary>
public static class ValidationMessages
{
    private static readonly ResourceManager Resources = new(
        "Omnichannel.Application.Resources.ValidationMessages",
        typeof(ValidationMessages).Assembly);

    /// <summary>Resource names. A unit test checks that each one has an English and a Vietnamese text.</summary>
    public static class Keys
    {
        public const string SlugFormat = nameof(SlugFormat);
        public const string SortByNotAllowed = nameof(SortByNotAllowed);
        public const string SortDirInvalid = nameof(SortDirInvalid);
        public const string PhoneCharacters = nameof(PhoneCharacters);
        public const string ItemsTooMany = nameof(ItemsTooMany);
        public const string ItemsContainNull = nameof(ItemsContainNull);
        public const string ItemsDuplicateProduct = nameof(ItemsDuplicateProduct);
        public const string DateRangeInverted = nameof(DateRangeInverted);
        public const string ExternalOrderIdTooLong = nameof(ExternalOrderIdTooLong);
        public const string SkuFormat = nameof(SkuFormat);
        public const string RoleUnknown = nameof(RoleUnknown);
        public const string EmailInvalid = nameof(EmailInvalid);
        public const string ProductNotFound = nameof(ProductNotFound);
        public const string CustomerNotFound = nameof(CustomerNotFound);
        public const string PasswordTooShort = nameof(PasswordTooShort);
        public const string PasswordRequiresDigit = nameof(PasswordRequiresDigit);
        public const string PasswordRequiresLower = nameof(PasswordRequiresLower);
        public const string PasswordRequiresUpper = nameof(PasswordRequiresUpper);
        public const string PasswordRequiresNonAlphanumeric = nameof(PasswordRequiresNonAlphanumeric);
        public const string PasswordRequiresUniqueChars = nameof(PasswordRequiresUniqueChars);
    }

    /// <summary>The text of <paramref name="key"/> in the current UI culture, with <c>{Name}</c> placeholders replaced.</summary>
    public static string Get(string key, params (string Name, object? Value)[] arguments) =>
        Format(Find(key, CultureInfo.CurrentUICulture), key, arguments);

    /// <summary>
    /// The text of <paramref name="key"/> exactly as stored for <paramref name="culture"/> (falls back to its parent
    /// cultures and then to English, like any resource lookup).
    /// </summary>
    public static string Find(string key, CultureInfo culture) =>
        Resources.GetString(key, culture)
        ?? throw new InvalidOperationException($"Validation message '{key}' is not defined in ValidationMessages.resx.");

    /// <summary>The text stored for exactly <paramref name="culture"/>, without fallback; null when that file lacks the key.</summary>
    public static string? FindExact(string key, CultureInfo culture) =>
        Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(key);

    private static string Format(string template, string key, (string Name, object? Value)[] arguments)
    {
        foreach (var (name, value) in arguments)
        {
            var placeholder = $"{{{name}}}";
            if (!template.Contains(placeholder, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Validation message '{key}' has no placeholder {placeholder}.");
            }

            template = template.Replace(placeholder, Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        return template;
    }
}
