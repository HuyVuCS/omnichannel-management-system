using System.Reflection;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Internal;

namespace Omnichannel.Application.Common.Validation;

/// <summary>
/// Validation error keys use the JSON (camelCase) names of the request, e.g. <c>items[0].productId</c>,
/// so the frontend can map every error to its form field.
/// </summary>
public static class ValidationConventions
{
    /// <summary>
    /// Sets FluentValidation's global property-name resolver. Process-wide and idempotent; called once at host start-up
    /// (and by the test assembly, so validator unit tests see the same keys as the API).
    /// </summary>
    public static void UseJsonPropertyNames() =>
        ValidatorOptions.Global.PropertyNameResolver = ResolvePropertyName;

    /// <summary>
    /// Sets FluentValidation's global display-name resolver: <c>{PropertyName}</c> in a message is the field's name from
    /// <see cref="FieldNames"/> in the UI culture of the request. FluentValidation asks for the name each time it builds a
    /// message, not when the rule is declared, so one validator instance answers in both languages. Process-wide and
    /// idempotent; called next to <see cref="UseJsonPropertyNames"/>.
    /// </summary>
    public static void UseLocalisedFieldNames() =>
        ValidatorOptions.Global.DisplayNameResolver = ResolveDisplayName;

    /// <summary>JSON name of a C# member name (<c>ProductId</c> → <c>productId</c>).</summary>
    public static string JsonName(string memberName) => JsonNamingPolicy.CamelCase.ConvertName(memberName);

    /// <summary>Key of a collection element's property, e.g. <c>ItemPath("Items", 1, "ProductId")</c> → <c>items[1].productId</c>.</summary>
    public static string ItemPath(string collectionName, int index, string memberName) =>
        $"{JsonName(collectionName)}[{index}].{JsonName(memberName)}";

    private static string? ResolvePropertyName(Type type, MemberInfo? member, System.Linq.Expressions.LambdaExpression? expression)
    {
        if (expression is not null)
        {
            var chain = PropertyChain.FromExpression(expression);
            if (chain.Count > 0)
            {
                // Nested member access (x => x.Address.Street): camelCase every segment.
                return string.Join('.', chain.ToString().Split('.').Select(JsonName));
            }
        }

        return member is null ? null : JsonName(member.Name);
    }

    // Null (no member, or a field without a name) falls back to FluentValidation's default; a unit test requires a name
    // for every validated field.
    private static string? ResolveDisplayName(Type type, MemberInfo? member, System.Linq.Expressions.LambdaExpression? expression) =>
        member is null ? null : FieldNames.Find(JsonName(member.Name));
}
