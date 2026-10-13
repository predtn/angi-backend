using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Auth;
using ANGI.Domain.Entities;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.Infrastructure.Services.Auth
{
    /// <summary>Issues JWTs, random opaque tokens, and SHA-256 hashes safe for persistence.</summary>
    public sealed class AuthenticationTokenService : IAuthenticationTokenService
    {
        private readonly JwtTokenSettings _settings;
        private readonly TimeProvider _timeProvider;

        /// <summary>Initializes the service with JWT settings and a replaceable time source for testing.</summary>
        public AuthenticationTokenService(
            IOptions<JwtTokenSettings> settings,
            TimeProvider timeProvider)
        {
            _settings = settings.Value;
            _timeProvider = timeProvider;
        }

        /// <summary>Creates an access token with user claims and a random 64-byte refresh token.</summary>
        public AuthenticationTokens CreateTokens(User user)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var accessTokenExpiresAt = now.AddMinutes(_settings.AccessTokenMinutes);
            var refreshTokenExpiresAt = now.AddDays(_settings.RefreshTokenDays);
            var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey));

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim("role", user.Role.Code),
                new Claim("email_verified", (user.EmailVerifiedAt.HasValue).ToString().ToLowerInvariant()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var jwt = new JwtSecurityToken(
                issuer: _settings.Issuer,
                audience: _settings.Audience,
                claims: claims,
                notBefore: now,
                expires: accessTokenExpiresAt,
                signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

            var refreshToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
            return new AuthenticationTokens(
                new JwtSecurityTokenHandler().WriteToken(jwt),
                accessTokenExpiresAt,
                refreshToken,
                HashRefreshToken(refreshToken),
                refreshTokenExpiresAt);
        }

        /// <summary>Converts a refresh token to lowercase SHA-256 hex so the database never stores the plain token.</summary>
        public string HashRefreshToken(string refreshToken)
        {
            return HashToken(refreshToken);
        }

        /// <summary>Creates a random 32-byte user token and its one-way hash.</summary>
        public GeneratedUserToken CreateUserToken()
        {
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            return new GeneratedUserToken(token, HashUserToken(token));
        }

        /// <summary>Converts a user token to lowercase SHA-256 hex for database lookup.</summary>
        public string HashUserToken(string token)
        {
            return HashToken(token);
        }

        /// <summary>Computes the common SHA-256 representation used by opaque authentication tokens.</summary>
        private static string HashToken(string token)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        }
    }
}
