using ANGI.WebApi.Common.Models;

namespace ANGI.WebApi.Configs
{
    /// <summary>Wraps the empty 404 / 405 responses of routing in ApiResponse.</summary>
    public static class StatusCodePagesConfig
    {
        private const string RouteNotFoundCode = "ROUTE_NOT_FOUND";
        private const string RouteNotFoundMessage = "Đường dẫn không tồn tại.";
        private const string MethodNotAllowedCode = "METHOD_NOT_ALLOWED";
        private const string MethodNotAllowedMessage = "Phương thức không được hỗ trợ cho đường dẫn này.";

        /// <summary>Runs only for responses without a body, so errors already written are left as they are.</summary>
        public static IApplicationBuilder UseApiStatusCodePages(this IApplicationBuilder app)
        {
            return app.UseStatusCodePages(context => WriteErrorAsync(context.HttpContext));
        }

        /// <summary>Writes ROUTE_NOT_FOUND for 404 and METHOD_NOT_ALLOWED for 405; other statuses are untouched.</summary>
        public static Task WriteErrorAsync(HttpContext context)
        {
            // STEP-01: Set (errorCode, message).
            var (errorCode, message) = context.Response.StatusCode switch
            {
                StatusCodes.Status404NotFound => (RouteNotFoundCode, RouteNotFoundMessage),
                StatusCodes.Status405MethodNotAllowed => (MethodNotAllowedCode, MethodNotAllowedMessage),
                _ => (null, null)
            };

            // STEP-02: Return Task.CompletedTask early when errorCode is missing.
            if (errorCode is null)
            {
                return Task.CompletedTask;
            }

            // STEP-03: Return the result of context.Response.WriteAsJsonAsync.
            return context.Response.WriteAsJsonAsync(new ApiResponse<object>
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode,
                Data = null
            });
        }
    }
}
