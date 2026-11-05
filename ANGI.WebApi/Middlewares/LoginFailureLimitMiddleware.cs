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
            // STEP-01: Stop here when HttpMethods.IsPost(context.Request.Method) does not hold or context.Request.Path differs from LoginPath.
            if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.Path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            // STEP-02: Prepare key through GetKey and now through timeProvider.GetUtcNow.
            var key = GetKey(context);
            var now = _timeProvider.GetUtcNow();

            // STEP-03: When TryGetActiveWindow(key, now, ...) holds and window.Failures >= _settings.LoginPermitLimit, prepare retryAfterSeconds through Math.Max and more.
            if (TryGetActiveWindow(key, now, out var window) && window.Failures >= _settings.LoginPermitLimit)
            {
                var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((window.ExpiresAt - now).TotalSeconds));
                await RateLimitConfig.WriteTooManyRequestsAsync(context, retryAfterSeconds, context.RequestAborted);
                return;
            }

            try
            {
                // STEP-04: Call next.
                await next(context);
            }
            catch (UnauthorizedException exception) when (exception.ErrorCode == InvalidCredentialsCode)
            {
                // STEP-05: On UnauthorizedException when exception.ErrorCode is InvalidCredentialsCode, call RegisterFailure and rethrow the caught exception.
                RegisterFailure(key, now);
                throw;
            }

            // STEP-06: When context.Response.StatusCode is StatusCodes.Status200OK, call cache.Remove.
            if (context.Response.StatusCode == StatusCodes.Status200OK)
            {
                _cache.Remove(key);
            }
        }

        private bool TryGetActiveWindow(string key, DateTimeOffset now, out FailureWindow window)
        {
            // STEP-01: Return true early when _cache has an entry for key, cached exists and cached.ExpiresAt > now.
            if (_cache.TryGetValue(key, out FailureWindow? cached) && cached is not null && cached.ExpiresAt > now)
            {
                window = cached;
                return true;
            }

            // STEP-02: Set window.
            window = null!;
            // STEP-03: Return false.
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

                // The cache evicts by its own clock, not TimeProvider, so give it a duration;
                // ExpiresAt (TimeProvider time) decides whether the window is still active.
                var duration = TimeSpan.FromMinutes(_settings.LoginWindowMinutes);
                _cache.Set(key, new FailureWindow { Failures = 1, ExpiresAt = now.Add(duration) }, duration);
            }
        }

        private static string GetKey(HttpContext context)
        {
            // STEP-01: Prepare email and ip from ToString.
            var email = context.Items[RateLimitPartitionMiddleware.EmailItemKey] as string ?? "unknown-email";
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
            // STEP-02: Return the formatted text.
            return $"login-failures:{email}|ip:{ip}";
        }

        private sealed class FailureWindow
        {
            public int Failures { get; set; }
            public DateTimeOffset ExpiresAt { get; init; }
        }
    }
}
