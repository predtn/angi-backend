using System.Net;
using System.Security.Cryptography;
using System.Text;
using ANGI.WebApi.Configs;
using Microsoft.Extensions.Options;

namespace ANGI.WebApi.Middlewares
{
    /// <summary>
    /// Takes the browser's IP from the Next.js server when the request carries the shared proxy key, so the per-IP rate
    /// limits and login-failure counts see each visitor instead of the frontend server. Without a matching key the
    /// headers are ignored: anyone can reach the API directly and could otherwise pick their own IP.
    /// </summary>
    public sealed class TrustedClientIpMiddleware : IMiddleware
    {
        public const string ProxyKeyHeader = "X-Angi-Proxy-Key";
        public const string ClientIpHeader = "X-Angi-Client-Ip";

        private readonly byte[] _proxyKey;

        public TrustedClientIpMiddleware(IOptions<TrustedClientIpSettings> settings)
        {
            _proxyKey = Encoding.UTF8.GetBytes(settings.Value.ProxyKey);
        }

        /// <summary>Replaces RemoteIpAddress with the forwarded client IP, then removes both headers.</summary>
        public Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            var headers = context.Request.Headers;
            if (_proxyKey.Length > 0 &&
                IsProxyKey(headers[ProxyKeyHeader].ToString()) &&
                IPAddress.TryParse(headers[ClientIpHeader].ToString().Trim(), out var clientIp))
            {
                context.Connection.RemoteIpAddress = clientIp.IsIPv4MappedToIPv6 ? clientIp.MapToIPv4() : clientIp;
            }

            headers.Remove(ProxyKeyHeader);
            headers.Remove(ClientIpHeader);
            return next(context);
        }

        /// <summary>Compares in constant time so the key cannot be guessed byte by byte from response timing.</summary>
        private bool IsProxyKey(string value)
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), _proxyKey);
        }
    }
}
