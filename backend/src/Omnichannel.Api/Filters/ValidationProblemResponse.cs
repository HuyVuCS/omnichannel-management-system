using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Omnichannel.Api.Filters;

/// <summary>
/// The single 400 shape for invalid input, used both for model-binding errors ([ApiController]) and
/// FluentValidation failures (<see cref="ValidationFilter"/>): ValidationProblemDetails with
/// <c>errorCode = "validation_failed"</c> and <c>correlationId</c> (added by ProblemDetails customisation).
/// </summary>
/// <remarks>
/// Error keys: FluentValidation errors use the request's JSON names (<c>items[0].productId</c>, see
/// <c>ValidationConventions</c>). Model-binding errors keep ASP.NET Core's own keys, which the frontend cannot map to a
/// single field in general: C# member names for query/route values (<c>PageSize</c>) and JSON paths for body errors
/// (<c>$.items[0].stock</c>). Those only occur for malformed input the UI does not produce.
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
        problem.Extensions["errorCode"] = ErrorCode;

        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" }
        };
    }
}
