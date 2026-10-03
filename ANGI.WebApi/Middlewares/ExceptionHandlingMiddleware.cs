using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.WebApi.Common.Models;
using FluentValidation;

namespace ANGI.WebApi.Middlewares
{
    public class ExceptionHandlingMiddleware : IMiddleware
    {
        private const string ValidationFailedCode = "VALIDATION_FAILED";
        private const string ValidationFailedMessage = "Dữ liệu không hợp lệ.";
        private const string InternalErrorCode = "INTERNAL_ERROR";
        private const string InternalErrorMessage = "Đã có lỗi xảy ra. Vui lòng thử lại sau.";

        private readonly ILogger<ExceptionHandlingMiddleware> _logger;

        public ExceptionHandlingMiddleware(ILogger<ExceptionHandlingMiddleware> logger)
        {
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            try
            {
                await next(context);
            }
            catch (Exception exception)
            {
                if (context.Response.HasStarted)
                {
                    _logger.LogError(exception, "Unhandled exception after the response has started.");
                    throw;
                }

                var (statusCode, response) = BuildResponse(exception);

                if (statusCode == StatusCodes.Status500InternalServerError)
                {
                    _logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
                }

                context.Response.Clear();
                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsJsonAsync(response);
            }
        }

        private static (int StatusCode, ApiResponse<object> Response) BuildResponse(Exception exception)
        {
            return exception switch
            {
                ValidationException validationException => (StatusCodes.Status400BadRequest, new ApiResponse<object>
                {
                    Success = false,
                    Message = ValidationFailedMessage,
                    ErrorCode = ValidationFailedCode,
                    Data = null,
                    Errors = GroupValidationErrors(validationException)
                }),
                AppException appException => (GetStatusCode(appException), new ApiResponse<object>
                {
                    Success = false,
                    Message = appException.Message,
                    ErrorCode = appException.ErrorCode,
                    Data = null
                }),
                _ => (StatusCodes.Status500InternalServerError, new ApiResponse<object>
                {
                    Success = false,
                    Message = InternalErrorMessage,
                    ErrorCode = InternalErrorCode,
                    Data = null
                })
            };
        }

        private static int GetStatusCode(AppException exception)
        {
            return exception switch
            {
                BadRequestException => StatusCodes.Status400BadRequest,
                UnauthorizedException => StatusCodes.Status401Unauthorized,
                ForbiddenException => StatusCodes.Status403Forbidden,
                NotFoundException => StatusCodes.Status404NotFound,
                ConflictException => StatusCodes.Status409Conflict,
                ServiceUnavailableException => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status500InternalServerError
            };
        }

        private static Dictionary<string, string[]> GroupValidationErrors(ValidationException exception)
        {
            return exception.Errors
                .GroupBy(failure => JsonNamingPolicy.CamelCase.ConvertName(failure.PropertyName))
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(failure => failure.ErrorMessage).Distinct().ToArray());
        }
    }
}
