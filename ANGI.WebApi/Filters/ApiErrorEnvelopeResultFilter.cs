using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ANGI.WebApi.Filters;

/// <summary>Normalizes framework-generated HTTP errors that bypass model-state handling.</summary>
public sealed class ApiErrorEnvelopeResultFilter : IAsyncResultFilter
{
    /// <summary>Maps an unsupported or absent request content type to the API's validation contract.</summary>
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is UnsupportedMediaTypeResult or ObjectResult { StatusCode: StatusCodes.Status415UnsupportedMediaType })
        {
            context.Result = new BadRequestObjectResult(new ApiResponse<object>
            {
                Success = false,
                Message = "Dữ liệu không hợp lệ.",
                ErrorCode = "VALIDATION_FAILED",
                Data = null,
                Errors = new Dictionary<string, string[]>
                {
                    ["body"] = ["Nội dung JSON là bắt buộc và phải có Content-Type application/json."]
                }
            });
        }

        return next();
    }
}
