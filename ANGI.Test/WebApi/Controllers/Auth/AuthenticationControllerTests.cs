using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace ANGI.Test.WebApi.Controllers.Auth
{
    public sealed class AuthenticationControllerTests
    {
        // TEST-01: Return the documented 201 response after registering an account.
        /// <summary>Verifies AUTH-01 is wrapped in ApiResponse with HTTP 201.</summary>
        [Fact]
        public async Task Register_ShouldReturnCreatedApiResponse()
        {
            var expected = new RegisterAccountResponseDto { UserId = 12, Status = "pending_verification" };
            var registerUseCase = new Mock<IRegisterAccountUseCase>();
            registerUseCase.Setup(x => x.ExecuteAsync(
                    It.IsAny<RegisterAccountRequestDto>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            var controller = CreateController(registerAccountUseCase: registerUseCase);

            var action = await controller.Register(new RegisterAccountRequestDto(), CancellationToken.None);

            var result = action.Should().BeOfType<ObjectResult>().Subject;
            result.StatusCode.Should().Be(StatusCodes.Status201Created);
            var response = result.Value.Should().BeOfType<ApiResponse<RegisterAccountResponseDto>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeSameAs(expected);
        }

        // TEST-02: Wrap a successful login result in the standard API response.
        /// <summary>Verifies AUTH-04 returns the login result in the standard success envelope.</summary>
        [Fact]
        public async Task Login_ShouldReturnOkApiResponse()
        {
            var expected = new AuthResultDto { AccessToken = "access-token" };
            var loginUseCase = new Mock<ILoginUseCase>();
            loginUseCase.Setup(x => x.ExecuteAsync(It.IsAny<LoginRequestDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            var controller = CreateController(loginUseCase: loginUseCase);

            var action = await controller.Login(new LoginRequestDto(), CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            var response = result.Value.Should().BeOfType<ApiResponse<AuthResultDto>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeSameAs(expected);
        }

        // TEST-03: Return a successful API response with null data after logout.
        /// <summary>Verifies AUTH-07 returns null data after delegating logout.</summary>
        [Fact]
        public async Task Logout_ShouldReturnOkApiResponseWithNullData()
        {
            var logoutUseCase = new Mock<ILogoutUseCase>();
            var controller = CreateController(logoutUseCase: logoutUseCase);

            var action = await controller.Logout(
                new LogoutRequestDto { RefreshToken = "refresh-token" },
                CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            result.StatusCode.Should().Be(StatusCodes.Status200OK);
            var response = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeNull();
            logoutUseCase.Verify(
                x => x.ExecuteAsync(It.IsAny<LogoutRequestDto>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // TEST-04: Wrap a successful token refresh in the documented authentication API response.
        /// <summary>Verifies AUTH-06 returns the rotated token result in the standard success envelope.</summary>
        [Fact]
        public async Task Refresh_ShouldReturnOkApiResponse()
        {
            var expected = new AuthResultDto { RefreshToken = "new-refresh-token" };
            var refreshUseCase = new Mock<IRefreshTokenUseCase>();
            refreshUseCase.Setup(x => x.ExecuteAsync(It.IsAny<RefreshTokenRequestDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            var controller = CreateController(refreshTokenUseCase: refreshUseCase);

            var action = await controller.Refresh(
                new RefreshTokenRequestDto { RefreshToken = "old-refresh-token" },
                CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            var response = result.Value.Should().BeOfType<ApiResponse<AuthResultDto>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeSameAs(expected);
        }

        // TEST-05: Wrap a successful email verification in the documented API response.
        /// <summary>Verifies AUTH-02 returns the verified account state from its use case.</summary>
        [Fact]
        public async Task VerifyEmail_ShouldReturnOkApiResponse()
        {
            var expected = new VerifyEmailResponseDto { UserId = 12, Status = "active" };
            var verifyEmailUseCase = new Mock<IVerifyEmailUseCase>();
            verifyEmailUseCase.Setup(x => x.ExecuteAsync(
                    It.IsAny<VerifyEmailRequestDto>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            var controller = CreateController(verifyEmailUseCase: verifyEmailUseCase);

            var action = await controller.VerifyEmail(
                new VerifyEmailRequestDto { Token = "raw-token" },
                CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            var response = result.Value.Should().BeOfType<ApiResponse<VerifyEmailResponseDto>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeSameAs(expected);
        }

        // TEST-06: Return a successful null-data response after requesting a verification resend.
        /// <summary>Verifies AUTH-03 keeps the response identical regardless of account existence.</summary>
        [Fact]
        public async Task ResendVerificationEmail_ShouldReturnOkApiResponseWithNullData()
        {
            var resendUseCase = new Mock<IResendVerificationEmailUseCase>();
            var controller = CreateController(resendVerificationEmailUseCase: resendUseCase);

            var action = await controller.ResendVerificationEmail(
                new ResendVerificationEmailRequestDto { Email = "user@angi.test" },
                CancellationToken.None);

            var result = action.Should().BeOfType<OkObjectResult>().Subject;
            var response = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
            response.Success.Should().BeTrue();
            response.Data.Should().BeNull();
            resendUseCase.Verify(x => x.ExecuteAsync(
                It.IsAny<ResendVerificationEmailRequestDto>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-07: Expose public authentication endpoints anonymously while requiring authentication for logout.
        /// <summary>Verifies the controller authorization attributes match the Auth API contract.</summary>
        [Fact]
        public void AuthenticationEndpoints_ShouldDeclareDocumentedAuthorizationAttributes()
        {
            var controllerType = typeof(AuthenticationController);

            controllerType.GetMethod(nameof(AuthenticationController.Register))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.Login))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.Refresh))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.VerifyEmail))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.ResendVerificationEmail))!
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Should().ContainSingle();
            controllerType.GetMethod(nameof(AuthenticationController.Logout))!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Should().ContainSingle();
        }

        /// <summary>Creates the controller with supplied mocks and harmless defaults for remaining dependencies.</summary>
        private static AuthenticationController CreateController(
            Mock<IRegisterAccountUseCase>? registerAccountUseCase = null,
            Mock<ILoginUseCase>? loginUseCase = null,
            Mock<IRefreshTokenUseCase>? refreshTokenUseCase = null,
            Mock<ILogoutUseCase>? logoutUseCase = null,
            Mock<IVerifyEmailUseCase>? verifyEmailUseCase = null,
            Mock<IResendVerificationEmailUseCase>? resendVerificationEmailUseCase = null)
        {
            return new AuthenticationController(
                (registerAccountUseCase ?? new Mock<IRegisterAccountUseCase>()).Object,
                (loginUseCase ?? new Mock<ILoginUseCase>()).Object,
                (refreshTokenUseCase ?? new Mock<IRefreshTokenUseCase>()).Object,
                (logoutUseCase ?? new Mock<ILogoutUseCase>()).Object,
                (verifyEmailUseCase ?? new Mock<IVerifyEmailUseCase>()).Object,
                (resendVerificationEmailUseCase ?? new Mock<IResendVerificationEmailUseCase>()).Object);
        }
    }
}

namespace ANGI.Test.WebApi.Controllers
{
    /// <summary>Exercises the AuthenticationController through the complete HTTP pipeline.</summary>
    public sealed class AuthenticationHttpContractTests : IClassFixture<AuthenticationApiFactory>
    {
        private readonly AuthenticationApiFactory _factory;

        public AuthenticationHttpContractTests(AuthenticationApiFactory factory)
        {
            _factory = factory;
        }

        public static TheoryData<string> BodyEndpoints => new()
        {
            "/api/v1/auth/verify-email",
            "/api/v1/auth/verify-email/resend"
        };

        // TEST-08: Reject a missing JSON body with the canonical validation envelope.
        [Theory]
        [MemberData(nameof(BodyEndpoints))]
        public async Task Endpoint_WithMissingBody_ShouldReturnValidationEnvelope(string path)
        {
            using var response = await _factory.CreateClient().PostAsync(path, content: null);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await AssertErrorEnvelope(response, "VALIDATION_FAILED", expectErrors: true);
        }

        // TEST-09: Reject malformed JSON with the canonical validation envelope.
        [Theory]
        [MemberData(nameof(BodyEndpoints))]
        public async Task Endpoint_WithMalformedJson_ShouldReturnValidationEnvelope(string path)
        {
            var malformed = $"{{\"email\":\"malformed-{Guid.NewGuid():N}@angi.test\"";
            using var content = new StringContent(malformed, Encoding.UTF8, "application/json");
            using var response = await _factory.CreateClient().PostAsync(path, content);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await AssertErrorEnvelope(response, "VALIDATION_FAILED", expectErrors: true);
        }

        // TEST-10: Reject a literal null JSON body with the canonical validation envelope.
        [Theory]
        [MemberData(nameof(BodyEndpoints))]
        public async Task Endpoint_WithNullJsonBody_ShouldReturnValidationEnvelope(string path)
        {
            using var content = new StringContent("null", Encoding.UTF8, "application/json");
            using var response = await _factory.CreateClient().PostAsync(path, content);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await AssertErrorEnvelope(response, "VALIDATION_FAILED", expectErrors: true);
        }

        // TEST-11: Map documented AUTH-02 application failures to their HTTP envelopes.
        [Theory]
        [InlineData("invalid-token", HttpStatusCode.BadRequest, "TOKEN_INVALID")]
        [InlineData("expired-token", HttpStatusCode.BadRequest, "TOKEN_EXPIRED")]
        [InlineData("used-token", HttpStatusCode.Conflict, "TOKEN_ALREADY_USED")]
        public async Task VerifyEmail_WithApplicationFailure_ShouldReturnDocumentedEnvelope(
            string token,
            HttpStatusCode expectedStatus,
            string expectedCode)
        {
            using var response = await _factory.CreateClient().PostAsJsonAsync(
                "/api/v1/auth/verify-email",
                new VerifyEmailRequestDto { Token = token });

            response.StatusCode.Should().Be(expectedStatus);
            await AssertErrorEnvelope(response, expectedCode);
        }

        // TEST-12: Map email-provider failure to the documented 503 envelope.
        [Fact]
        public async Task ResendVerificationEmail_WhenEmailProviderFails_ShouldReturnServiceUnavailableEnvelope()
        {
            using var response = await _factory.CreateClient().PostAsJsonAsync(
                "/api/v1/auth/verify-email/resend",
                new ResendVerificationEmailRequestDto { Email = "service-unavailable@angi.test" });

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            await AssertErrorEnvelope(response, "SERVICE_UNAVAILABLE");
        }

        // TEST-13: Return 429, Retry-After, and the canonical envelope on the second resend per email.
        [Fact]
        public async Task ResendVerificationEmail_TwiceWithinWindow_ShouldReturnRateLimitEnvelope()
        {
            await using var factory = new AuthenticationApiFactory(
                sensitiveWindowSeconds: 60,
                sensitivePermitLimit: 1);
            var email = $"rate-{Guid.NewGuid():N}@angi.test";
            var client = factory.CreateClient();

            using var first = await client.PostAsJsonAsync(
                "/api/v1/auth/verify-email/resend",
                new ResendVerificationEmailRequestDto { Email = email });
            using var second = await client.PostAsJsonAsync(
                "/api/v1/auth/verify-email/resend",
                new ResendVerificationEmailRequestDto { Email = email });

            first.StatusCode.Should().Be(HttpStatusCode.OK);
            second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            second.Headers.RetryAfter.Should().NotBeNull();
            second.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero);
            second.Headers.RetryAfter.Delta.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
            await AssertErrorEnvelope(second, "TOO_MANY_REQUESTS");
        }

        // TEST-14: Admit a resend again after the configured fixed window replenishes.
        [Fact]
        public async Task ResendVerificationEmail_AfterWindow_ShouldBeAllowedAgain()
        {
            await using var factory = new AuthenticationApiFactory(
                sensitiveWindowSeconds: 1,
                sensitivePermitLimit: 1);
            var client = factory.CreateClient();
            var email = $"reset-{Guid.NewGuid():N}@angi.test";

            using var first = await client.PostAsJsonAsync(
                "/api/v1/auth/verify-email/resend",
                new ResendVerificationEmailRequestDto { Email = email });
            using var limited = await client.PostAsJsonAsync(
                "/api/v1/auth/verify-email/resend",
                new ResendVerificationEmailRequestDto { Email = email });
            await Task.Delay(TimeSpan.FromMilliseconds(2200));
            using var replenished = await client.PostAsJsonAsync(
                "/api/v1/auth/verify-email/resend",
                new ResendVerificationEmailRequestDto { Email = email });

            first.StatusCode.Should().Be(HttpStatusCode.OK);
            limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            replenished.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // TEST-15: Expose AUTH-02 and AUTH-03 request and success schemas to API Explorer/OpenAPI.
        [Fact]
        public void ApiExplorer_ShouldExposeAuthenticationVerificationSchemas()
        {
            var descriptions = _factory.Services
                .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
                .ApiDescriptionGroups.Items
                .SelectMany(group => group.Items)
                .ToArray();

            AssertOpenApiContract<VerifyEmailRequestDto, VerifyEmailResponseDto>(
                descriptions,
                "api/v1/auth/verify-email");
            AssertOpenApiContract<ResendVerificationEmailRequestDto, object>(
                descriptions,
                "api/v1/auth/verify-email/resend");
        }

        private static void AssertOpenApiContract<TRequest, TResponse>(
            IEnumerable<ApiDescription> descriptions,
            string relativePath)
        {
            var description = descriptions.Single(item =>
                item.HttpMethod == HttpMethod.Post.Method &&
                item.RelativePath == relativePath);

            description.ParameterDescriptions.Should().ContainSingle(parameter =>
                parameter.Source.Id == "Body" &&
                parameter.ModelMetadata.ModelType == typeof(TRequest));
            description.SupportedResponseTypes.Should().ContainSingle(response =>
                response.StatusCode == (int)HttpStatusCode.OK &&
                response.Type == typeof(ApiResponse<TResponse>));
        }

        private static async Task AssertErrorEnvelope(
            HttpResponseMessage response,
            string expectedCode,
            bool expectErrors = false)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("success").GetBoolean().Should().BeFalse();
            root.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
            root.GetProperty("errorCode").GetString().Should().Be(expectedCode);
            root.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
            if (expectErrors)
            {
                root.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();
            }
        }
    }

    /// <summary>Hosts the authentication HTTP contract tests with deterministic local dependencies.</summary>
    public sealed class AuthenticationApiFactory : WebApplicationFactory<Program>
    {
        private readonly int _sensitiveWindowSeconds;
        private readonly int _sensitivePermitLimit;

        public AuthenticationApiFactory()
            : this(60, 100)
        {
        }

        internal AuthenticationApiFactory(int sensitiveWindowSeconds, int sensitivePermitLimit)
        {
            _sensitiveWindowSeconds = sensitiveWindowSeconds;
            _sensitivePermitLimit = sensitivePermitLimit;
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            const string secret = "ANGI-58-http-contract-test-secret-key-123456789";
            var previousSecret = Environment.GetEnvironmentVariable("Jwt__SecretKey");
            var previousPermitLimit = Environment.GetEnvironmentVariable("RateLimit__SensitiveAuthPermitLimit");
            var previousWindowSeconds = Environment.GetEnvironmentVariable("RateLimit__SensitiveAuthWindowSeconds");
            Environment.SetEnvironmentVariable("Jwt__SecretKey", secret);
            Environment.SetEnvironmentVariable(
                "RateLimit__SensitiveAuthPermitLimit",
                _sensitivePermitLimit.ToString());
            Environment.SetEnvironmentVariable(
                "RateLimit__SensitiveAuthWindowSeconds",
                _sensitiveWindowSeconds.ToString());
            try
            {
                return base.CreateHost(builder);
            }
            finally
            {
                Environment.SetEnvironmentVariable("Jwt__SecretKey", previousSecret);
                Environment.SetEnvironmentVariable("RateLimit__SensitiveAuthPermitLimit", previousPermitLimit);
                Environment.SetEnvironmentVariable("RateLimit__SensitiveAuthWindowSeconds", previousWindowSeconds);
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Issuer"] = "ANGI.Auth.HttpTests",
                    ["Jwt:Audience"] = "ANGI.Auth.HttpTests.Client",
                    ["Jwt:SecretKey"] = "ANGI-58-http-contract-test-secret-key-123456789",
                    ["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused",
                    ["RateLimit:GeneralPermitLimit"] = "100",
                    ["RateLimit:GeneralWindowSeconds"] = "60",
                    ["RateLimit:LoginPermitLimit"] = "5",
                    ["RateLimit:LoginWindowMinutes"] = "15",
                    ["RateLimit:SensitiveAuthPermitLimit"] = _sensitivePermitLimit.ToString(),
                    ["RateLimit:SensitiveAuthWindowSeconds"] = _sensitiveWindowSeconds.ToString()
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVerifyEmailUseCase>();
                services.RemoveAll<IResendVerificationEmailUseCase>();
                services.AddScoped<IVerifyEmailUseCase, StubVerifyEmailUseCase>();
                services.AddScoped<IResendVerificationEmailUseCase, StubResendVerificationEmailUseCase>();
            });
        }

        private sealed class StubVerifyEmailUseCase : IVerifyEmailUseCase
        {
            public Task<VerifyEmailResponseDto> ExecuteAsync(VerifyEmailRequestDto request, CancellationToken ct)
            {
                return request.Token switch
                {
                    "invalid-token" => throw new BadRequestException("TOKEN_INVALID", "Token không hợp lệ."),
                    "expired-token" => throw new BadRequestException("TOKEN_EXPIRED", "Token đã hết hạn."),
                    "used-token" => throw new ConflictException("TOKEN_ALREADY_USED", "Token đã được sử dụng."),
                    _ => Task.FromResult(new VerifyEmailResponseDto
                    {
                        UserId = 12,
                        EmailVerifiedAt = DateTime.UtcNow,
                        Status = "active"
                    })
                };
            }
        }

        private sealed class StubResendVerificationEmailUseCase : IResendVerificationEmailUseCase
        {
            public Task ExecuteAsync(ResendVerificationEmailRequestDto request, CancellationToken ct)
            {
                if (request.Email.Equals("service-unavailable@angi.test", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ServiceUnavailableException("SERVICE_UNAVAILABLE", "Không gửi được email.");
                }

                return Task.CompletedTask;
            }
        }
    }
}
