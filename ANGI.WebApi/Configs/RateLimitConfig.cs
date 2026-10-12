using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Middlewares;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ANGI.WebApi.Configs
{
    public static class RateLimitConfig
    {
        private const string TooManyRequestsCode = "TOO_MANY_REQUESTS";
        private const string TooManyRequestsMessage = "Bạn đã gửi quá nhiều yêu cầu. Vui lòng thử lại sau.";
        private const string ResendVerificationPath = "/api/v1/auth/verify-email/resend";
        private const string ForgotPasswordPath = "/api/v1/auth/forgot-password";

        /// <summary>Registers global API and sensitive-auth fixed-window rate limiters.</summary>
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

            // Login counts failed attempts only, which the rate limiter cannot see before the request runs
            services.AddMemoryCache();
            services.TryAddSingleton(TimeProvider.System);
            services.AddScoped<LoginFailureLimitMiddleware>();

            services.AddRateLimiter(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    CreatePartition(context, settings));

                options.OnRejected = async (context, cancellationToken) =>
                {
                    var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var duration)
                        ? Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds))
                        : settings.GeneralWindowSeconds;

                    await WriteTooManyRequestsAsync(context.HttpContext, retryAfter, cancellationToken);
                };
            });

            return services;
        }

        /// <summary>Writes the canonical 429 API response and Retry-After header.</summary>
        public static async Task WriteTooManyRequestsAsync(HttpContext context, int retryAfterSeconds, CancellationToken cancellationToken)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await context.Response.WriteAsJsonAsync(new ApiResponse<object>
            {
                Success = false,
                Message = TooManyRequestsMessage,
                ErrorCode = TooManyRequestsCode,
                Data = null
            }, cancellationToken);
        }

        /// <summary>Selects the applicable limiter and partition key for an incoming request.</summary>
        private static RateLimitPartition<string> CreatePartition(
            HttpContext context,
            RateLimitSettings settings)
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                return RateLimitPartition.GetNoLimiter("non-api");
            }

            var path = context.Request.Path;
            if (path.Equals(ResendVerificationPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(ForgotPasswordPath, StringComparison.OrdinalIgnoreCase))
            {
                return CreateFixedWindowPartition(
                    $"sensitive-auth:{GetEmailKey(context)}",
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

        /// <summary>Creates a fixed-window rate-limit partition with no request queue.</summary>
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

        /// <summary>Builds the sensitive-auth partition from normalized email, falling back to IP for unreadable bodies.</summary>
        private static string GetEmailKey(HttpContext context)
        {
            var email = context.Items[RateLimitPartitionMiddleware.EmailItemKey] as string;
            return email ?? $"unknown-email|ip:{GetIpAddress(context)}";
        }

        /// <summary>Returns the remote IP address or a stable fallback when it is unavailable.</summary>
        private static string GetIpAddress(HttpContext context)
        {
            return context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
        }

        /// <summary>Checks that every configured rate-limit threshold and window is positive.</summary>
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
