using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Middlewares;
using Microsoft.AspNetCore.RateLimiting;

namespace ANGI.WebApi.Configs
{
    public static class RateLimitConfig
    {
        private const string TooManyRequestsCode = "TOO_MANY_REQUESTS";
        private const string TooManyRequestsMessage = "Bạn đã gửi quá nhiều yêu cầu. Vui lòng thử lại sau.";
        private const string LoginPath = "/api/v1/auth/login";
        private const string ResendVerificationPath = "/api/v1/auth/verify-email/resend";
        private const string ForgotPasswordPath = "/api/v1/auth/forgot-password";

        public static IServiceCollection AddRateLimitConfiguration(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var section = configuration.GetSection(RateLimitSettings.SectionName);
            var settings = section.Get<RateLimitSettings>() ?? new RateLimitSettings();

            services.AddOptions<RateLimitSettings>()
                .Bind(section)
                .Validate(AreValid, "All rate-limit values must be greater than zero.")
                .ValidateOnStart();

            services.AddRateLimiter(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    CreatePartition(context, settings));

                options.OnRejected = async (context, cancellationToken) =>
                {
                    var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var duration)
                        ? Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds))
                        : settings.GeneralWindowSeconds;

                    context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                    await context.HttpContext.Response.WriteAsJsonAsync(new ApiResponse<object>
                    {
                        Success = false,
                        Message = TooManyRequestsMessage,
                        ErrorCode = TooManyRequestsCode,
                        Data = null
                    }, cancellationToken);
                };
            });

            return services;
        }

        private static RateLimitPartition<string> CreatePartition(
            HttpContext context,
            RateLimitSettings settings)
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                return RateLimitPartition.GetNoLimiter("non-api");
            }

            var path = context.Request.Path;
            if (path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase))
            {
                return CreateFixedWindowPartition(
                    $"login:{GetEmailAndIpKey(context)}",
                    settings.LoginPermitLimit,
                    TimeSpan.FromMinutes(settings.LoginWindowMinutes));
            }

            if (path.Equals(ResendVerificationPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(ForgotPasswordPath, StringComparison.OrdinalIgnoreCase))
            {
                return CreateFixedWindowPartition(
                    $"sensitive-auth:{GetEmailAndIpKey(context)}",
                    settings.SensitiveAuthPermitLimit,
                    TimeSpan.FromSeconds(settings.SensitiveAuthWindowSeconds));
            }

            var userId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var clientKey = string.IsNullOrWhiteSpace(userId)
                ? $"ip:{GetIpAddress(context)}"
                : $"user:{userId}";

            return CreateFixedWindowPartition(
                $"general:{clientKey}",
                settings.GeneralPermitLimit,
                TimeSpan.FromSeconds(settings.GeneralWindowSeconds));
        }

        private static RateLimitPartition<string> CreateFixedWindowPartition(
            string partitionKey,
            int permitLimit,
            TimeSpan window)
        {
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0,
                AutoReplenishment = true
            });
        }

        private static string GetEmailAndIpKey(HttpContext context)
        {
            var email = context.Items[RateLimitPartitionMiddleware.EmailItemKey] as string;
            return $"{email ?? "unknown-email"}|ip:{GetIpAddress(context)}";
        }

        private static string GetIpAddress(HttpContext context)
        {
            return context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
        }

        private static bool AreValid(RateLimitSettings settings)
        {
            return settings.GeneralPermitLimit > 0 &&
                   settings.GeneralWindowSeconds > 0 &&
                   settings.LoginPermitLimit > 0 &&
                   settings.LoginWindowMinutes > 0 &&
                   settings.SensitiveAuthPermitLimit > 0 &&
                   settings.SensitiveAuthWindowSeconds > 0;
        }
    }
}
