using ANGI.Application.Common.Exceptions;
using ANGI.WebApi.Configs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ANGI.WebApi.Middlewares
{
    // API Design rate limit for login: 5 failed attempts per 15 minutes per email + IP.
    // A failure is a login that ends in INVALID_CREDENTIALS; other errors and successes do not count,
    // and a successful login clears the counter. Must run after RateLimitPartitionMiddleware (email).
    public sealed class LoginFailureLimitMiddleware : IMiddleware
    {
        public const string LoginPath = "/api/v1/auth/login";
        private const string InvalidCredentialsCode = "INVALID_CREDENTIALS";
        private static readonly object CounterLock = new();

        private readonly IMemoryCache _cache;
        private readonly TimeProvider _timeProvider;
        private readonly RateLimitSettings _settings;

        public LoginFailureLimitMiddleware(IMemoryCache cache, TimeProvider timeProvider, IOptions<RateLimitSettings> settings)
        {
            _cache = cache;
            _timeProvider = timeProvider;
            _settings = settings.Value;
        }

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.Path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            var key = GetKey(context);
            var now = _timeProvider.GetUtcNow();

            if (TryGetActiveWindow(key, now, out var window) && window.Failures >= _settings.LoginPermitLimit)
            {
                var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((window.ExpiresAt - now).TotalSeconds));
                await RateLimitConfig.WriteTooManyRequestsAsync(context, retryAfterSeconds, context.RequestAborted);
                return;
            }

            try
            {
                await next(context);
            }
            catch (UnauthorizedException exception) when (exception.ErrorCode == InvalidCredentialsCode)
            {
                RegisterFailure(key, now);
                throw;
            }

            if (context.Response.StatusCode == StatusCodes.Status200OK)
            {
                _cache.Remove(key);
            }
        }

        private bool TryGetActiveWindow(string key, DateTimeOffset now, out FailureWindow window)
        {
            if (_cache.TryGetValue(key, out FailureWindow? cached) && cached is not null && cached.ExpiresAt > now)
            {
                window = cached;
                return true;
            }

            window = null!;
            return false;
        }

        private void RegisterFailure(string key, DateTimeOffset now)
        {
            lock (CounterLock)
            {
                if (TryGetActiveWindow(key, now, out var window))
                {
                    window.Failures++;
                    return;
                }

                var expiresAt = now.AddMinutes(_settings.LoginWindowMinutes);
                _cache.Set(key, new FailureWindow { Failures = 1, ExpiresAt = expiresAt }, expiresAt);
            }
        }

        private static string GetKey(HttpContext context)
        {
            var email = context.Items[RateLimitPartitionMiddleware.EmailItemKey] as string ?? "unknown-email";
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
            return $"login-failures:{email}|ip:{ip}";
        }

        private sealed class FailureWindow
        {
            public int Failures { get; set; }
            public DateTimeOffset ExpiresAt { get; init; }
        }
    }
}
