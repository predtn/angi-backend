using System.Net;
using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.WebApi.Configs;
using ANGI.WebApi.Middlewares;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.WebApi.Middlewares
{
    /// <summary>Verifies LoginFailureLimitMiddleware limits repeated failed logins per email and IP and lifts the limit on time.</summary>
    public class LoginFailureLimitMiddlewareTests
    {
        private const int PermitLimit = 3;
        private const int WindowMinutes = 15;

        private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero));
        private readonly LoginFailureLimitMiddleware _middleware;

        public LoginFailureLimitMiddlewareTests()
        {
            var settings = Options.Create(new RateLimitSettings
            {
                GeneralPermitLimit = 100,
                GeneralWindowSeconds = 60,
                LoginPermitLimit = PermitLimit,
                LoginWindowMinutes = WindowMinutes,
                SensitiveAuthPermitLimit = 1,
                SensitiveAuthWindowSeconds = 60
            });
            _middleware = new LoginFailureLimitMiddleware(new MemoryCache(new MemoryCacheOptions()), _time, settings);
        }

        // TEST-01: Verify that the login after the configured number of failures is rejected with 429 and Retry-After.
        /// <summary>Ensures brute-force attempts are stopped with 429 and a retry time.</summary>
        [Fact]
        public async Task InvokeAsync_ShouldRejectWithTooManyRequests_AfterConfiguredFailures()
        {
            // STEP-01: Assert on every iteration, FailLoginAsync succeeds.
            for (var attempt = 0; attempt < PermitLimit; attempt++)
            {
                await FailLoginAsync();
            }

            // STEP-02: Define nextCalled as false and create context through CreateLoginContext.
            var nextCalled = false;
            var context = CreateLoginContext();
            // STEP-03: Run _middleware.InvokeAsync with context.
            await _middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

            // STEP-04: Assert nextCalled is false, context.Response.StatusCode is StatusCodes.Status429TooManyRequests and 2 more.
            nextCalled.Should().BeFalse();
            context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            context.Response.Headers.RetryAfter.ToString().Should().Be((WindowMinutes * 60).ToString());
            ReadErrorCode(context).Should().Be("TOO_MANY_REQUESTS");
        }

        // TEST-02: Verify that a failed login is still rethrown so the exception middleware returns 401.
        /// <summary>Ensures a failed login still produces the normal 401 response.</summary>
        [Fact]
        public async Task InvokeAsync_ShouldRethrowInvalidCredentials()
        {
            // STEP-01: Wrap _middleware.InvokeAsync in act so its outcome can be asserted.
            var act = () => _middleware.InvokeAsync(CreateLoginContext(), _ => throw InvalidCredentials());

            // STEP-02: Assert act throws UnauthorizedException.
            await act.Should().ThrowAsync<UnauthorizedException>();
        }

        // TEST-03: Verify that a successful login clears the failure counter.
        /// <summary>Ensures a successful login resets the user's failure count.</summary>
        [Fact]
        public async Task InvokeAsync_ShouldClearFailures_AfterSuccessfulLogin()
        {
            // STEP-01: Assert on every iteration, FailLoginAsync succeeds.
            for (var attempt = 0; attempt < PermitLimit - 1; attempt++)
            {
                await FailLoginAsync();
            }

            // STEP-02: Run _middleware.InvokeAsync with SucceedAsync.
            await _middleware.InvokeAsync(CreateLoginContext(), SucceedAsync);
            // STEP-03: Assert on every iteration, FailLoginAsync succeeds.
            for (var attempt = 0; attempt < PermitLimit - 1; attempt++)
            {
                await FailLoginAsync();
            }

            // STEP-04: Create context through CreateLoginContext.
            var context = CreateLoginContext();
            // STEP-05: Run _middleware.InvokeAsync with context and SucceedAsync.
            await _middleware.InvokeAsync(context, SucceedAsync);
            // STEP-06: Assert context.Response.StatusCode is StatusCodes.Status200OK.
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        // TEST-04: Verify that only INVALID_CREDENTIALS counts as a failure.
        /// <summary>Ensures only wrong credentials count toward the limit.</summary>
        [Fact]
        public async Task InvokeAsync_ShouldNotCount_OtherErrors()
        {
            // STEP-01: Assert on every iteration, forbidden throws ForbiddenException and otherUnauthorized throws UnauthorizedException.
            for (var attempt = 0; attempt < PermitLimit; attempt++)
            {
                var forbidden = () => _middleware.InvokeAsync(CreateLoginContext(),
                    _ => throw new ForbiddenException("ACCOUNT_SUSPENDED", "Tài khoản đang bị tạm khóa."));
                await forbidden.Should().ThrowAsync<ForbiddenException>();

                var otherUnauthorized = () => _middleware.InvokeAsync(CreateLoginContext(),
                    _ => throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập."));
                await otherUnauthorized.Should().ThrowAsync<UnauthorizedException>();

                await _middleware.InvokeAsync(CreateLoginContext(), context =>
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return Task.CompletedTask;
                });
            }

            // STEP-02: Define nextCalled as false.
            var nextCalled = false;
            // STEP-03: Run _middleware.InvokeAsync.
            await _middleware.InvokeAsync(CreateLoginContext(), _ => { nextCalled = true; return Task.CompletedTask; });
            // STEP-04: Assert nextCalled is true.
            nextCalled.Should().BeTrue();
        }

        // TEST-05: Verify that Retry-After counts down and the limit lifts when the window ends.
        /// <summary>Ensures the limit is temporary and Retry-After stays accurate.</summary>
        [Fact]
        public async Task InvokeAsync_ShouldAllowLoginAgain_WhenWindowEnds()
        {
            // STEP-01: Assert on every iteration, FailLoginAsync succeeds.
            for (var attempt = 0; attempt < PermitLimit; attempt++)
            {
                await FailLoginAsync();
            }

            // STEP-02: Advance _time and create blocked through CreateLoginContext.
            _time.Advance(TimeSpan.FromMinutes(10));
            var blocked = CreateLoginContext();
            // STEP-03: Run _middleware.InvokeAsync with blocked and SucceedAsync.
            await _middleware.InvokeAsync(blocked, SucceedAsync);
            // STEP-04: Assert blocked.Response.StatusCode is StatusCodes.Status429TooManyRequests and blocked.Response.Headers.RetryAfter.ToString() is 5 * 60.ToString().
            blocked.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            blocked.Response.Headers.RetryAfter.ToString().Should().Be((5 * 60).ToString());

            // STEP-05: Advance _time and create allowed through CreateLoginContext.
            _time.Advance(TimeSpan.FromMinutes(5));
            var allowed = CreateLoginContext();
            // STEP-06: Run _middleware.InvokeAsync with allowed and SucceedAsync.
            await _middleware.InvokeAsync(allowed, SucceedAsync);
            // STEP-07: Assert allowed.Response.StatusCode is StatusCodes.Status200OK.
            allowed.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        // TEST-06: Verify that failures are counted per email and per IP address.
        /// <summary>Ensures one account or address cannot exhaust another's attempts.</summary>
        [Fact]
        public async Task InvokeAsync_ShouldCountPerEmailAndIp()
        {
            // STEP-01: Assert on every iteration, FailLoginAsync succeeds.
            for (var attempt = 0; attempt < PermitLimit; attempt++)
            {
                await FailLoginAsync();
            }

            // STEP-02: Create otherEmail through CreateLoginContext.
            var otherEmail = CreateLoginContext(email: "other@angi.test");
            // STEP-03: Run _middleware.InvokeAsync with otherEmail and SucceedAsync.
            await _middleware.InvokeAsync(otherEmail, SucceedAsync);
            // STEP-04: Assert otherEmail.Response.StatusCode is StatusCodes.Status200OK.
            otherEmail.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

            // STEP-05: Create otherIp through CreateLoginContext.
            var otherIp = CreateLoginContext(ip: IPAddress.Parse("10.0.0.2"));
            // STEP-06: Run _middleware.InvokeAsync with otherIp and SucceedAsync.
            await _middleware.InvokeAsync(otherIp, SucceedAsync);
            // STEP-07: Assert otherIp.Response.StatusCode is StatusCodes.Status200OK.
            otherIp.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        // TEST-07: Verify that requests other than POST /api/v1/auth/login are never limited.
        /// <summary>Ensures the limit applies only to the login endpoint.</summary>
        [Fact]
        public async Task InvokeAsync_ShouldIgnoreOtherRequests()
        {
            // STEP-01: Assert on every iteration, FailLoginAsync succeeds.
            for (var attempt = 0; attempt < PermitLimit; attempt++)
            {
                await FailLoginAsync();
            }

            // STEP-02: Create otherPath through CreateLoginContext and set otherPath.Request.Path to "/api/v1/auth/refresh".
            var otherPath = CreateLoginContext();
            otherPath.Request.Path = "/api/v1/auth/refresh";
            // STEP-03: Run _middleware.InvokeAsync with otherPath and SucceedAsync.
            await _middleware.InvokeAsync(otherPath, SucceedAsync);
            // STEP-04: Assert otherPath.Response.StatusCode is StatusCodes.Status200OK.
            otherPath.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

            // STEP-05: Create getLogin through CreateLoginContext and set getLogin.Request.Method to HttpMethods.Get.
            var getLogin = CreateLoginContext();
            getLogin.Request.Method = HttpMethods.Get;
            // STEP-06: Run _middleware.InvokeAsync with getLogin and SucceedAsync.
            await _middleware.InvokeAsync(getLogin, SucceedAsync);
            // STEP-07: Assert getLogin.Response.StatusCode is StatusCodes.Status200OK.
            getLogin.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        /// <summary>Runs one login request that fails with invalid credentials and asserts the 401 is rethrown.</summary>
        private async Task FailLoginAsync()
        {
            var act = () => _middleware.InvokeAsync(CreateLoginContext(), _ => throw InvalidCredentials());
            await act.Should().ThrowAsync<UnauthorizedException>();
        }

        /// <summary>Pipeline step that completes the login with 200.</summary>
        private static Task SucceedAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }

        /// <summary>Creates the INVALID_CREDENTIALS exception the login use case throws.</summary>
        private static UnauthorizedException InvalidCredentials()
        {
            return new UnauthorizedException("INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng.");
        }

        /// <summary>Creates a POST login request for the given email and IP address.</summary>
        private static DefaultHttpContext CreateLoginContext(string email = "user@angi.test", IPAddress? ip = null)
        {
            var context = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
            };
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = LoginFailureLimitMiddleware.LoginPath;
            context.Connection.RemoteIpAddress = ip ?? IPAddress.Loopback;
            context.Items[RateLimitPartitionMiddleware.EmailItemKey] = email;
            context.Response.Body = new MemoryStream();
            return context;
        }

        /// <summary>Reads errorCode from the JSON response body.</summary>
        private static string? ReadErrorCode(HttpContext context)
        {
            context.Response.Body.Position = 0;
            using var document = JsonDocument.Parse(context.Response.Body);
            return document.RootElement.GetProperty("errorCode").GetString();
        }

        /// <summary>Clock that only moves when the test advances it, so window expiry can be tested without waiting.</summary>
        private sealed class ManualTimeProvider : TimeProvider
        {
            private DateTimeOffset _now;

            public ManualTimeProvider(DateTimeOffset now) => _now = now;

            /// <summary>Returns the current manual instant.</summary>
            public override DateTimeOffset GetUtcNow() => _now;

            /// <summary>Moves the manual clock forward by the given duration.</summary>
            public void Advance(TimeSpan duration) => _now = _now.Add(duration);
        }
    }
}
