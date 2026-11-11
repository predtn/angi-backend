using System.Security.Claims;
using ANGI.Infrastructure.Services.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace ANGI.UnitTests.Infrastructure.Services.Auth
{
    /// <summary>Verifies CurrentUserService reads the user id, role and session id from the access token claims.</summary>
    public sealed class CurrentUserServiceTests
    {
        // TEST-01: Read the session id from the sid claim; a missing or non-numeric sid gives null.
        /// <summary>Ensures only a numeric sid claim identifies the current session.</summary>
        [Theory]
        [InlineData("77", 77L)]
        [InlineData("not-a-number", null)]
        [InlineData("-5", null)]
        [InlineData(null, null)]
        public void SessionId_ShouldComeFromTheSidClaim(string? sid, long? expected)
        {
            // STEP-01: Create claims as List<Claim>, when sid is not null, add a Claim to claims and create httpContext as DefaultHttpContext.
            var claims = new List<Claim> { new("sub", "3") };
            if (sid is not null)
            {
                claims.Add(new Claim("sid", sid));
            }

            var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
            // STEP-02: Create the CurrentUserService under test as service.
            var service = new CurrentUserService(new HttpContextAccessor { HttpContext = httpContext });

            // STEP-03: Assert service.SessionId is expected.
            service.SessionId.Should().Be(expected);
        }

        // TEST-02: Read the user id and role from the sub and role claims of the access token.
        /// <summary>Ensures the caller's identity comes from the token claims.</summary>
        [Fact]
        public void UserIdAndRole_WithAuthenticatedRequest_ShouldComeFromClaims()
        {
            // STEP-01: Create httpContext as DefaultHttpContext.
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "3"), new Claim("role", "MOD")], "Bearer"))
            };
            // STEP-02: Create the CurrentUserService under test as service.
            var service = new CurrentUserService(new HttpContextAccessor { HttpContext = httpContext });

            // STEP-03: Assert service.UserId is 3 and service.Role is "MOD".
            service.UserId.Should().Be(3);
            service.Role.Should().Be("MOD");
        }

        // TEST-03: Return no user id or role for an anonymous request or outside a request.
        /// <summary>Ensures anonymous requests and background work have no identity.</summary>
        [Fact]
        public void UserIdAndRole_WithoutToken_ShouldBeNull()
        {
            // STEP-01: Create the CurrentUserService under test as anonymous; create the CurrentUserService under test as noRequest.
            var anonymous = new CurrentUserService(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
            var noRequest = new CurrentUserService(new HttpContextAccessor());

            // STEP-02: Assert anonymous.UserId is null, anonymous.Role is null, noRequest.UserId is null and noRequest.Role is null.
            anonymous.UserId.Should().BeNull();
            anonymous.Role.Should().BeNull();
            noRequest.UserId.Should().BeNull();
            noRequest.Role.Should().BeNull();
        }
    }
}
