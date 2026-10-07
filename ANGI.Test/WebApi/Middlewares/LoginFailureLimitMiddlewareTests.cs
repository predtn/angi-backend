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

namespace ANGI.Test.WebApi.Middlewares
{
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
        [Fact]
        public async Task InvokeAsync_ShouldRejectWithTooManyRequests_AfterConfiguredFailures()
        {
            for (var attempt = 0; attempt < PermitLimit; attempt++)
            {
                await FailLoginAsync();
            }

            var nextCalled = false;
            var context = CreateLoginContext();
            await _middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

            nextCalled.Should().BeFalse();
            context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            context.Response.Headers.RetryAfter.ToString().Should().Be((WindowMinutes * 60).ToString());
            ReadErrorCode(context).Should().Be("TOO_MANY_REQUESTS");
        }

        // TEST-02: Verify that a failed login is still rethrown so the exception middleware returns 401.
        [Fact]
        public async Task InvokeAsync_ShouldRethrowInvalidCredentials()
        {
            var act = () => _middleware.InvokeAsync(CreateLoginContext(), _ => throw InvalidCredentials());

            await act.Should().ThrowAsync<UnauthorizedException>();
        }

        // TEST-03: Verify that a successful login clears the failure counter.
        [Fact]
        public async Task InvokeAsync_ShouldClearFailures_AfterSuccessfulLogin()
        {
            for (var attempt = 0; attempt < PermitLimit - 1; attempt++)
            {
                await FailLoginAsync();
            }

            await _middleware.InvokeAsync(CreateLoginContext(), SucceedAsync);
            for (var attempt = 0; attempt < PermitLimit - 1; attempt++)
            {
                await FailLoginAsync();
            }

            var context = CreateLoginContext();
            await _middleware.InvokeAsync(context, SucceedAsync);
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        // TEST-04: Verify that only INVALID_CREDENTIALS counts as a failure.
        [Fact]
        public async Task InvokeAsync_ShouldNotCount_OtherErrors()
        {
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

            var nextCalled = false;
            await _middleware.InvokeAsync(CreateLoginContext(), _ => { nextCalled = true; return Task.CompletedTask; });
            nextCalled.Should().BeTrue();
        }

        // TEST-05: Verify that Retry-After counts down and the limit lifts when the window ends.
        [Fact]
        public async Task InvokeAsync_ShouldAllowLoginAgain_WhenWindowEnds()
        {
            for (var attempt = 0; attempt < PermitLimit; attempt++)
            {
                await FailLoginAsync();
            }

            _time.Advance(TimeSpan.FromMinutes(10));
            var blocked = CreateLoginContext();
            await _middleware.InvokeAsync(blocked, SucceedAsync);
            blocked.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            blocked.Response.Headers.RetryAfter.ToString().Should().Be((5 * 60).ToString());

            _time.Advance(TimeSpan.FromMinutes(5));
            var allowed = CreateLoginContext();
            await _middleware.InvokeAsync(allowed, SucceedAsync);
            allowed.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        // TEST-06: Verify that failures are counted per email and per IP address.
        [Fact]
        public async Task InvokeAsync_ShouldCountPerEmailAndIp()
        {
            for (var attempt = 0; attempt < PermitLimit; attempt++)
            {
                await FailLoginAsync();
            }

            var otherEmail = CreateLoginContext(email: "other@angi.test");
            await _middleware.InvokeAsync(otherEmail, SucceedAsync);
            otherEmail.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

            var otherIp = CreateLoginContext(ip: IPAddress.Parse("10.0.0.2"));
            await _middleware.InvokeAsync(otherIp, SucceedAsync);
            otherIp.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        // TEST-07: Verify that requests other than POST /api/v1/auth/login are never limited.
        [Fact]
        public async Task InvokeAsync_ShouldIgnoreOtherRequests()
        {
            for (var attempt = 0; attempt < PermitLimit; attempt++)
            {
                await FailLoginAsync();
            }

            var otherPath = CreateLoginContext();
            otherPath.Request.Path = "/api/v1/auth/refresh";
            await _middleware.InvokeAsync(otherPath, SucceedAsync);
            otherPath.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

            var getLogin = CreateLoginContext();
            getLogin.Request.Method = HttpMethods.Get;
            await _middleware.InvokeAsync(getLogin, SucceedAsync);
            getLogin.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        private async Task FailLoginAsync()
        {
            var act = () => _middleware.InvokeAsync(CreateLoginContext(), _ => throw InvalidCredentials());
            await act.Should().ThrowAsync<UnauthorizedException>();
        }

        private static Task SucceedAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }

        private static UnauthorizedException InvalidCredentials()
        {
            return new UnauthorizedException("INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng.");
        }

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

        private static string? ReadErrorCode(HttpContext context)
        {
            context.Response.Body.Position = 0;
            using var document = JsonDocument.Parse(context.Response.Body);
            return document.RootElement.GetProperty("errorCode").GetString();
        }

        private sealed class ManualTimeProvider : TimeProvider
        {
            private DateTimeOffset _now;

            public ManualTimeProvider(DateTimeOffset now) => _now = now;

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan duration) => _now = _now.Add(duration);
        }
    }
}
