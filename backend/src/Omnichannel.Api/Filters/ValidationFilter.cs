using System.Collections.Concurrent;
using FluentValidation;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Omnichannel.Api.Filters;

/// <summary>
/// Runs the FluentValidation validator registered for each action argument (if any) and
/// short-circuits with a 400 ValidationProblemDetails - same shape as MVC model-binding errors.
/// </summary>
public sealed class ValidationFilter : IAsyncActionFilter
{
    private static readonly ConcurrentDictionary<Type, Type> ValidatorTypes = new();

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            var validatorType = ValidatorTypes.GetOrAdd(
                argument.GetType(),
                static type => typeof(IValidator<>).MakeGenericType(type));

            if (services.GetService(validatorType) is not IValidator validator)
            {
                continue;
            }

            var result = await validator.ValidateAsync(
                new ValidationContext<object>(argument),
                context.HttpContext.RequestAborted);

            foreach (var error in result.Errors)
            {
                context.ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            }
        }

        if (!context.ModelState.IsValid)
        {
            context.Result = ValidationProblemResponse.Create(context.HttpContext, context.ModelState);
            return;
        }

        await next();
    }
}
