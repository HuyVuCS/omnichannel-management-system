using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Omnichannel.Api.Filters;

/// <summary>
/// The single 400 shape for invalid input, used both for model-binding errors ([ApiController]) and
/// FluentValidation failures (<see cref="ValidationFilter"/>): ValidationProblemDetails with
/// <c>errorCode = "validation_failed"</c> and <c>correlationId</c> (added by ProblemDetails customisation).
/// </summary>
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
