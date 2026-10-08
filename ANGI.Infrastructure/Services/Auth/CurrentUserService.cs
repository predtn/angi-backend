using System.IdentityModel.Tokens.Jwt;
using System.Net;
using ANGI.Application.Common.Interfaces.Services.Auth;
using Microsoft.AspNetCore.Http;

namespace ANGI.Infrastructure.Services.Auth
{
    /// <summary>Reads the user id, role, User-Agent, and IP address from the current HttpContext.</summary>
    public sealed class CurrentUserService : ICurrentUserService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        /// <summary>Initializes the adapter with the accessor provided by ASP.NET Core.</summary>
        public CurrentUserService(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        /// <summary>Gets the user id from the sub claim, returning null when it is missing or not numeric.</summary>
        public int? UserId
        {
            get
            {
                var value = _httpContextAccessor.HttpContext?.User
                    .FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                return int.TryParse(value, out var userId) ? userId : null;
            }
        }

        /// <summary>Gets the role code from the role claim (JwtConfig sets RoleClaimType = "role").</summary>
        public string? Role => _httpContextAccessor.HttpContext?.User.FindFirst("role")?.Value;

        /// <summary>Gets the User-Agent of the current request.</summary>
        public string? UserAgent => _httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString();

        /// <summary>Gets the remote IP address of the current request.</summary>
        public IPAddress? IpAddress => _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress;
    }
}
