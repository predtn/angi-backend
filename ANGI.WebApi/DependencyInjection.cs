using ANGI.WebApi.Common;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Configs;
using ANGI.WebApi.Middlewares;
using ANGI.WebApi.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

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
            services.AddScoped<NullBodyValidationFilter>();

            services.AddControllers(options =>
                {
                    // Required fields are checked only by FluentValidation (coding_rule.md, Validation): without this,
                    // MVC rejects a null non-nullable reference first, with an English message.
                    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
                    options.Filters.Add<ApiErrorEnvelopeResultFilter>();
                    options.Filters.Add<NullBodyValidationFilter>();
                })
                .ConfigureApiBehaviorOptions(options =>
                {
                    options.InvalidModelStateResponseFactory = context =>
                    {
                        var bodyParameterNames = context.ActionDescriptor.Parameters
                            .Where(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Body)
                            .Select(parameter => parameter.Name)
                            .ToArray();
                        var errors = ModelBindingErrors.From(context.ModelState, bodyParameterNames);

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

            services.AddOpenApiDocumentation();
            services.AddJwtConfiguration(configuration);
            services.AddCorsConfiguration(configuration);
            services.AddRateLimitConfiguration(configuration);

            return services;
        }
    }
}
