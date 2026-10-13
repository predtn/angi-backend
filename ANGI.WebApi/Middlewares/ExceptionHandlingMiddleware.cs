using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using FluentValidation;

namespace ANGI.WebApi.Middlewares
{
    /// <summary>Converts application and unexpected exceptions into the canonical API envelope.</summary>
    public class ExceptionHandlingMiddleware : IMiddleware
    {
        private const string ValidationFailedCode = "VALIDATION_FAILED";
        private const string ValidationFailedMessage = "Dữ liệu không hợp lệ.";
        private const string InternalErrorCode = "INTERNAL_ERROR";
        private const string InternalErrorMessage = "Đã có lỗi xảy ra. Vui lòng thử lại sau.";

        private readonly ILogger<ExceptionHandlingMiddleware> _logger;

        /// <summary>Initializes exception handling with structured logging.</summary>
        public ExceptionHandlingMiddleware(ILogger<ExceptionHandlingMiddleware> logger)
        {
            _logger = logger;
        }

        /// <summary>Executes the next pipeline component and serializes any thrown error consistently.</summary>
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

        /// <summary>Maps a known exception to its documented status, error code, data, and validation errors.</summary>
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
                AccountSuspendedException suspendedException => (StatusCodes.Status403Forbidden, new ApiResponse<object>
                {
                    Success = false,
                    Message = suspendedException.Message,
                    ErrorCode = suspendedException.ErrorCode,
                    Data = new AccountSuspendedErrorDto
                    {
                        SuspendedUntil = suspendedException.SuspendedUntil
                    }
                }),
                SubmissionAlreadyOpenException openException => (StatusCodes.Status409Conflict, new ApiResponse<object>
                {
                    Success = false,
                    Message = openException.Message,
                    ErrorCode = openException.ErrorCode,
                    Data = new SubmissionAlreadyOpenErrorDto { Id = openException.SubmissionId }
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

        /// <summary>Maps an application exception category to its allowed HTTP status.</summary>
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

        /// <summary>Groups FluentValidation failures by camel-case JSON property name.</summary>
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
