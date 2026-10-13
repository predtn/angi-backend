using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Filters;

/// <summary>Rejects a JSON literal null before a controller receives its required request body.</summary>
public sealed class NullBodyValidationFilter : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var missingRequiredBody = context.ActionDescriptor.Parameters
            .OfType<ControllerParameterDescriptor>()
            .Where(parameter => parameter.BindingInfo?.BindingSource?.CanAcceptDataFrom(BindingSource.Body) == true)
            .Any(parameter => !context.ActionArguments.TryGetValue(parameter.Name, out var value) || value is null);

        if (!missingRequiredBody)
        {
            return next();
        }

        context.Result = new BadRequestObjectResult(new ApiResponse<object>
        {
            Success = false,
            Message = "Dữ liệu không hợp lệ.",
            ErrorCode = "VALIDATION_FAILED",
            Data = null,
            Errors = new Dictionary<string, string[]>
            {
                ["body"] = ["Nội dung JSON là bắt buộc và không được là null."]
            }
        });
        return Task.CompletedTask;
    }
}
