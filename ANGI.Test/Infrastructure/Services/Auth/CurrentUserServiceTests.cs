using System.Security.Claims;
using ANGI.Infrastructure.Services.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace ANGI.Test.Infrastructure.Services.Auth
{
    public sealed class CurrentUserServiceTests
    {
        // TEST-03: Read the session id from the sid claim; a missing or non-numeric sid gives null.
        [Theory]
        [InlineData("77", 77L)]
        [InlineData("not-a-number", null)]
        [InlineData("-5", null)]
        [InlineData(null, null)]
        public void SessionId_ShouldComeFromTheSidClaim(string? sid, long? expected)
        {
            var claims = new List<Claim> { new("sub", "3") };
            if (sid is not null)
            {
                claims.Add(new Claim("sid", sid));
            }

            var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
            var service = new CurrentUserService(new HttpContextAccessor { HttpContext = httpContext });

            service.SessionId.Should().Be(expected);
        }

        // TEST-01: Read the user id and role from the sub and role claims of the access token.
        [Fact]
        public void UserIdAndRole_WithAuthenticatedRequest_ShouldComeFromClaims()
        {
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "3"), new Claim("role", "MOD")], "Bearer"))
            };
            var service = new CurrentUserService(new HttpContextAccessor { HttpContext = httpContext });

            service.UserId.Should().Be(3);
            service.Role.Should().Be("MOD");
        }

        // TEST-02: Return no user id or role for an anonymous request or outside a request.
        [Fact]
        public void UserIdAndRole_WithoutToken_ShouldBeNull()
        {
            var anonymous = new CurrentUserService(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
            var noRequest = new CurrentUserService(new HttpContextAccessor());

            anonymous.UserId.Should().BeNull();
            anonymous.Role.Should().BeNull();
            noRequest.UserId.Should().BeNull();
            noRequest.Role.Should().BeNull();
        }
    }
}
