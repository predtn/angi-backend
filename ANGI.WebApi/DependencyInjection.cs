using System.Text.Json;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Configs;
using ANGI.WebApi.Middlewares;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddWebApi(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.AddScoped<ExceptionHandlingMiddleware>();
            services.AddScoped<RateLimitPartitionMiddleware>();
            services.AddScoped<AccountStatusMiddleware>();

            services.AddControllers()
                .ConfigureApiBehaviorOptions(options =>
                {
                    options.InvalidModelStateResponseFactory = context =>
                    {
                        var errors = context.ModelState
                            .Where(entry => entry.Value?.Errors.Count > 0)
                            .ToDictionary(
                                entry => JsonNamingPolicy.CamelCase.ConvertName(entry.Key),
                                entry => entry.Value!.Errors
                                    .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                                        ? "Giá trị không hợp lệ."
                                        : error.ErrorMessage)
                                    .Distinct()
                                    .ToArray());

                        return new BadRequestObjectResult(new ApiResponse<object>
                        {
                            Success = false,
                            Message = "Dữ liệu không hợp lệ.",
                            ErrorCode = "VALIDATION_FAILED",
                            Data = null,
                            Errors = errors
                        });
                    };
                });

            services.AddOpenApi();
            services.AddJwtConfiguration(configuration);
            services.AddCorsConfiguration(configuration);
            services.AddRateLimitConfiguration(configuration);

            return services;
        }
    }
}
