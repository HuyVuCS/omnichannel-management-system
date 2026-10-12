using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Api.Filters;

/// <summary>
/// The single 400 shape for invalid input, used both for model-binding errors ([ApiController]) and
/// FluentValidation failures (<see cref="ValidationFilter"/>): ValidationProblemDetails with
/// <c>errorCode = "validation_failed"</c> and <c>correlationId</c> (added by ProblemDetails customisation).
/// </summary>
/// <remarks>
/// Error keys are camelCase JSON paths for every source: body fields (<c>items[0].productId</c>), query-string
/// parameters (<c>to</c>, <c>sortBy</c>) and model-binding errors (<c>pageSize</c>). They are normalised here because
/// <see cref="ModelStateDictionary"/> is case-insensitive: for a supplied query parameter model binding registers the
/// entry under the C# member name (<c>To</c>) and a validator error for <c>to</c> is merged into it.
/// <para>
/// A body the JSON parser could not read (not JSON, a text where a number belongs, an unknown enum value, a number out
/// of range) arrives here under the parser's path (<c>$</c>, <c>$.items[0].stock</c>) with a text that names .NET types.
/// Neither is passed on: the key becomes the same camelCase path a validator would use (<c>items[0].stock</c>; the
/// body itself stays <c>$</c>) and the message a generic one in the language of the request
/// (<see cref="ValidationMessages.Keys.ValueInvalid"/>, <see cref="ValidationMessages.Keys.BodyNotJson"/>). The error
/// MVC adds for the action parameter that could not be bound ("The request field is required.") is dropped with it.
/// </para>
/// The empty key is left as it is.
/// </remarks>
public static class ValidationProblemResponse
{
    public const string ErrorCode = "validation_failed";

    public static IActionResult Create(HttpContext httpContext, ModelStateDictionary modelState)
    {
        var factory = httpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(
            httpContext,
            modelState,
            StatusCodes.Status400BadRequest,
            title: "Validation failed");
        problem.Errors = NormaliseKeys(WithoutParserTexts(problem.Errors));
        problem.Extensions["errorCode"] = ErrorCode;

        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" }
        };
    }

    /// <summary>The key of an error about the body as a whole (not JSON, or not the expected kind of JSON value).</summary>
    public const string BodyKey = "$";

    /// <summary>
    /// Replaces what the JSON parser reported (keys starting with <c>$</c>) by the field's own key and a generic
    /// localised message. When the parser reported anything, only its errors are kept: nothing was bound, so every
    /// other entry is a consequence of the same failure.
    /// </summary>
    public static IDictionary<string, string[]> WithoutParserTexts(IDictionary<string, string[]> errors)
    {
        if (!errors.Keys.Any(IsParserPath))
        {
            return errors;
        }

        var replaced = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var path in errors.Keys.Where(IsParserPath))
        {
            var key = path.Length > 2 && path[1] == '.' ? NormaliseKey(path[2..]) : BodyKey;
            replaced[key] = key == BodyKey
                ? [ValidationMessages.Get(ValidationMessages.Keys.BodyNotJson)]
                : [ValidationMessages.Get(ValidationMessages.Keys.ValueInvalid, ("PropertyName", FieldNameOf(key)))];
        }

        return replaced;
    }

    private static bool IsParserPath(string key) => key.Length > 0 && key[0] == '$';

    /// <summary>The localised name of the last member of a path (<c>items[0].stock</c> → "Quantity"), or the path itself.</summary>
    private static string FieldNameOf(string key)
    {
        var member = key[(key.LastIndexOf('.') + 1)..];
        var index = member.IndexOf('[', StringComparison.Ordinal);
        return FieldNames.Find(index < 0 ? member : member[..index]) ?? key;
    }

    /// <summary>
    /// The same errors under camelCase keys (see <see cref="NormaliseKey"/>). Keys that collapse into one keep all their
    /// messages, in order, without repeating a message.
    /// </summary>
    public static Dictionary<string, string[]> NormaliseKeys(IDictionary<string, string[]> errors)
    {
        var normalised = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (key, messages) in errors)
        {
            var name = NormaliseKey(key);
            normalised[name] = normalised.TryGetValue(name, out var existing)
                ? existing.Union(messages, StringComparer.Ordinal).ToArray()
                : messages;
        }

        return normalised;
    }

    /// <summary>
    /// camelCases the name of every <c>.</c>-separated segment and keeps its <c>[n]</c> indexes:
    /// <c>Items[0].ProductId</c> → <c>items[0].productId</c>. Keys starting with <c>$</c> and the empty key are unchanged.
    /// </summary>
    public static string NormaliseKey(string key)
    {
        if (key.Length == 0 || key[0] == '$')
        {
            return key;
        }

        return string.Join('.', key.Split('.').Select(segment =>
        {
            var index = segment.IndexOf('[', StringComparison.Ordinal);
            var name = index < 0 ? segment : segment[..index];
            return name.Length == 0 ? segment : ValidationConventions.JsonName(name) + segment[name.Length..];
        }));
    }
}
