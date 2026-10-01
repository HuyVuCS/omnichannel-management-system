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
/// The body paths System.Text.Json reports for malformed JSON (<c>$</c>, <c>$.items[0].stock</c>) and the empty key
/// are left as they are.
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
        problem.Errors = NormaliseKeys(problem.Errors);
        problem.Extensions["errorCode"] = ErrorCode;

        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" }
        };
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
