using System.Text.Json;

namespace ANGI.WebApi.Middlewares
{
    public sealed class RateLimitPartitionMiddleware : IMiddleware
    {
        public const string EmailItemKey = "RateLimitEmail";
        private const int MaxPartitionBodyBytes = 64 * 1024;

        private static readonly PathString[] EmailRateLimitedPaths =
        [
            "/api/v1/auth/login",
            "/api/v1/auth/verify-email/resend",
            "/api/v1/auth/forgot-password"
        ];

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (HttpMethods.IsPost(context.Request.Method) &&
                EmailRateLimitedPaths.Any(path => context.Request.Path.Equals(path)))
            {
                await SetNormalizedEmailAsync(context);
            }

            await next(context);
        }

        private static async Task SetNormalizedEmailAsync(HttpContext context)
        {
            context.Request.EnableBuffering();

            try
            {
                var buffer = new byte[MaxPartitionBodyBytes + 1];
                var bytesRead = 0;

                while (bytesRead < buffer.Length)
                {
                    var read = await context.Request.Body.ReadAsync(
                        buffer.AsMemory(bytesRead, buffer.Length - bytesRead),
                        context.RequestAborted);
                    if (read == 0)
                    {
                        break;
                    }

                    bytesRead += read;
                }

                if (bytesRead > MaxPartitionBodyBytes)
                {
                    return;
                }

                using var document = JsonDocument.Parse(buffer.AsMemory(0, bytesRead));

                if (document.RootElement.TryGetProperty("email", out var emailElement) &&
                    emailElement.ValueKind == JsonValueKind.String)
                {
                    var email = emailElement.GetString()?.Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(email))
                    {
                        context.Items[EmailItemKey] = email;
                    }
                }
            }
            catch (JsonException)
            {
                // MVC model binding returns the canonical validation response for malformed JSON.
            }
            finally
            {
                context.Request.Body.Position = 0;
            }
        }
    }
}
