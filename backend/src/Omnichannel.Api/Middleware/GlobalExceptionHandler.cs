using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Common.Exceptions;

namespace Omnichannel.Api.Middleware;

/// <summary>
/// Maps exceptions to RFC 7807 ProblemDetails. Unexpected exceptions return a generic 500
/// without internal details; the full exception is logged with the correlation id.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request aborted by the client");
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        ProblemDetails problem = exception switch
        {
            ValidationException validation => CreateValidationProblem(validation),
            NotFoundException notFound => Create(StatusCodes.Status404NotFound, "Resource not found", notFound.Message, "not_found"),
            ConflictException conflict => Create(StatusCodes.Status409Conflict, "Conflict", conflict.Message, conflict.ErrorCode),
            DbUpdateConcurrencyException => Create(
                StatusCodes.Status409Conflict,
                "Concurrency conflict",
                "The resource was modified by another request. Reload and try again.",
                "concurrency_conflict"),
            _ => Create(
                StatusCodes.Status500InternalServerError,
                "An unexpected error occurred",
                "The error has been logged. Quote the correlation id when reporting it.",
                "internal_error")
        };

        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception");
        }
        else
        {
            logger.LogInformation("Request failed with {StatusCode}: {ErrorCode}", problem.Status, problem.Extensions["errorCode"]);
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    private static ProblemDetails Create(int status, string title, string detail, string errorCode)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["errorCode"] = errorCode;
        return problem;
    }

    private static ValidationProblemDetails CreateValidationProblem(ValidationException exception)
    {
        var errors = exception.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        var problem = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failed"
        };
        problem.Extensions["errorCode"] = "validation_failed";
        return problem;
    }
}
