using System.IdentityModel.Tokens.Jwt;
using ANGI.Domain.Entities;
using ANGI.Infrastructure.Services.Auth;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ANGI.Test.Infrastructure.Services.Auth
{
    public sealed class AuthenticationTokenServiceTests
    {
        private static readonly DateTime Now = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

        // TEST-01: Issue a signed access token with the required identity claims and configured lifetime.
        [Fact]
        public void CreateTokens_ShouldIssueRequiredJwtClaimsAndExpiry()
        {
            var service = CreateService();
            var user = new User
            {
                Id = 12,
                Email = "user@angi.test",
                EmailVerifiedAt = Now.AddDays(-1),
                Role = new Role { Code = "TRAVELER", Name = "Traveler" }
            };

            var tokens = service.CreateTokens(user);
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken);

            jwt.Issuer.Should().Be("ANGI.Tests");
            jwt.Audiences.Should().ContainSingle("ANGI.Client.Tests");
            jwt.Claims.Should().Contain(claim => claim.Type == JwtRegisteredClaimNames.Sub && claim.Value == "12");
            jwt.Claims.Should().Contain(claim => claim.Type == JwtRegisteredClaimNames.Email && claim.Value == "user@angi.test");
            jwt.Claims.Should().Contain(claim => claim.Type == "role" && claim.Value == "TRAVELER");
            jwt.Claims.Should().Contain(claim => claim.Type == "email_verified" && claim.Value == "true");
            tokens.AccessTokenExpiresAt.Should().Be(Now.AddMinutes(15));
            tokens.RefreshTokenExpiresAt.Should().Be(Now.AddDays(30));
        }

        // TEST-02: Store only a deterministic SHA-256 hash while issuing a random refresh token.
        [Fact]
        public void CreateTokens_ShouldReturnHashedRefreshToken()
        {
            var service = CreateService();
            var user = new User
            {
                Id = 12,
                Email = "user@angi.test",
                Role = new Role { Code = "TRAVELER", Name = "Traveler" }
            };

            var first = service.CreateTokens(user);
            var second = service.CreateTokens(user);

            first.RefreshToken.Should().NotBe(first.RefreshTokenHash);
            first.RefreshTokenHash.Should().Be(service.HashRefreshToken(first.RefreshToken));
            first.RefreshTokenHash.Should().HaveLength(64);
            second.RefreshToken.Should().NotBe(first.RefreshToken);
        }

        private static AuthenticationTokenService CreateService()
        {
            return new AuthenticationTokenService(
                Options.Create(new JwtTokenSettings
                {
                    Issuer = "ANGI.Tests",
                    Audience = "ANGI.Client.Tests",
                    SecretKey = "test-secret-key-that-is-at-least-32-bytes-long",
                    AccessTokenMinutes = 15,
                    RefreshTokenDays = 30
                }),
                new FixedTimeProvider(Now));
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _now;

            public FixedTimeProvider(DateTime now)
            {
                _now = new DateTimeOffset(now);
            }

            public override DateTimeOffset GetUtcNow() => _now;
        }
    }
}
