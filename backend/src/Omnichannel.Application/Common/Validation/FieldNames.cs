using System.Globalization;
using System.Resources;

namespace Omnichannel.Application.Common.Validation;

/// <summary>
/// What a request field is called inside validation messages, in the UI culture of the request: "Low-stock threshold" /
/// "Ngưỡng cảnh báo tồn kho" instead of the technical name. Names live in <c>Resources/FieldNames.resx</c> (English, the
/// default) and <c>FieldNames.vi.resx</c>, keyed by the JSON member name (<c>lowStockThreshold</c>) - one key per name, not
/// per request type - and are worded like the matching label of the UI.
/// <para>
/// FluentValidation fills <c>{PropertyName}</c> from here (<see cref="ValidationConventions.UseLocalisedFieldNames"/>). A
/// message that names a second field passes it as an argument:
/// <c>ValidationMessages.Get(Keys.DateRangeInverted, ("From", FieldNames.ForMember(nameof(Query.From))))</c>.
/// </para>
/// Error keys are never translated: they stay the JSON paths.
/// </summary>
public static class FieldNames
{
    private static readonly ResourceManager Resources = new(
        "Omnichannel.Application.Resources.FieldNames",
        typeof(FieldNames).Assembly);

    /// <summary>The name of the field with JSON member name <paramref name="jsonName"/> in the current UI culture.</summary>
    public static string Get(string jsonName) =>
        Find(jsonName)
        ?? throw new InvalidOperationException($"Field name '{jsonName}' is not defined in FieldNames.resx.");

    /// <summary>Like <see cref="Get"/>, for a C# member name (<c>LowStockThreshold</c>).</summary>
    public static string ForMember(string memberName) => Get(ValidationConventions.JsonName(memberName));

    /// <summary>Like <see cref="Get"/>, but null when the name is not defined.</summary>
    public static string? Find(string jsonName) => Resources.GetString(jsonName, CultureInfo.CurrentUICulture);

    /// <summary>The name stored for exactly <paramref name="culture"/>, without fallback; null when that file lacks the key.</summary>
    public static string? FindExact(string jsonName, CultureInfo culture) =>
        Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(jsonName);
}
