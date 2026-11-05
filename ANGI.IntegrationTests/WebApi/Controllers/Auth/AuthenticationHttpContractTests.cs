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

namespace ANGI.IntegrationTests.WebApi.Controllers.Auth
{
    /// <summary>Exercises the AuthenticationController through the complete HTTP pipeline.</summary>
    public sealed class AuthenticationHttpContractTests : IClassFixture<AuthenticationApiFactory>
    {
        private readonly AuthenticationApiFactory _factory;

        /// <summary>Initializes HTTP contract tests with the shared deterministic host.</summary>
        public AuthenticationHttpContractTests(AuthenticationApiFactory factory)
        {
            _factory = factory;
        }

        /// <summary>Lists public Auth endpoints whose contracts require a JSON request body.</summary>
        public static TheoryData<string> BodyEndpoints => new()
        {
            "/api/v1/auth/google",
            "/api/v1/auth/verify-email",
            "/api/v1/auth/verify-email/resend",
            "/api/v1/auth/forgot-password",
            "/api/v1/auth/reset-password"
        };

        // TEST-01: Reject a missing JSON body with the canonical validation envelope.
        /// <summary>Verifies body-required Auth endpoints reject an absent request payload.</summary>
        [Theory]
        [MemberData(nameof(BodyEndpoints))]
        public async Task Endpoint_WithMissingBody_ShouldReturnValidationEnvelope(string path)
        {
            // STEP-01: Send POST path with no body and keep response.
            using var response = await _factory.CreateClient().PostAsync(path, content: null);

            // STEP-02: Assert response.StatusCode is HttpStatusCode.BadRequest and AssertErrorEnvelope confirms VALIDATION_FAILED.
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await AssertErrorEnvelope(response, "VALIDATION_FAILED", expectErrors: true);
        }

        // TEST-02: Reject malformed JSON with the canonical validation envelope.
        /// <summary>Verifies malformed Auth JSON is converted to the standard validation error.</summary>
        [Theory]
        [MemberData(nameof(BodyEndpoints))]
        public async Task Endpoint_WithMalformedJson_ShouldReturnValidationEnvelope(string path)
        {
            // STEP-01: Define malformed as the expected text and create content as StringContent.
            var malformed = $"{{\"email\":\"malformed-{Guid.NewGuid():N}@angi.test\"";
            using var content = new StringContent(malformed, Encoding.UTF8, "application/json");
            // STEP-02: Send POST path with content and keep response.
            using var response = await _factory.CreateClient().PostAsync(path, content);

            // STEP-03: Assert response.StatusCode is HttpStatusCode.BadRequest and AssertErrorEnvelope confirms VALIDATION_FAILED.
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await AssertErrorEnvelope(response, "VALIDATION_FAILED", expectErrors: true);
        }

        // TEST-03: Reject a literal null JSON body with the canonical validation envelope.
        /// <summary>Verifies a JSON null cannot bypass required Auth request models.</summary>
        [Theory]
        [MemberData(nameof(BodyEndpoints))]
        public async Task Endpoint_WithNullJsonBody_ShouldReturnValidationEnvelope(string path)
        {
            // STEP-01: Create content as StringContent.
            using var content = new StringContent("null", Encoding.UTF8, "application/json");
            // STEP-02: Send POST path with content and keep response.
            using var response = await _factory.CreateClient().PostAsync(path, content);

            // STEP-03: Assert response.StatusCode is HttpStatusCode.BadRequest and AssertErrorEnvelope confirms VALIDATION_FAILED.
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await AssertErrorEnvelope(response, "VALIDATION_FAILED", expectErrors: true);
        }

        // TEST-04: Map documented AUTH-02 application failures to their HTTP envelopes.
        /// <summary>Verifies every AUTH-02 token failure keeps its documented status and error code.</summary>
        [Theory]
        [InlineData("invalid-token", HttpStatusCode.BadRequest, "TOKEN_INVALID")]
        [InlineData("expired-token", HttpStatusCode.BadRequest, "TOKEN_EXPIRED")]
        [InlineData("used-token", HttpStatusCode.Conflict, "TOKEN_ALREADY_USED")]
        public async Task VerifyEmail_WithApplicationFailure_ShouldReturnDocumentedEnvelope(
            string token,
            HttpStatusCode expectedStatus,
            string expectedCode)
        {
            // STEP-01: Send POST /api/v1/auth/verify-email with a VerifyEmailRequestDto body and keep response.
            using var response = await _factory.CreateClient().PostAsJsonAsync(
                "/api/v1/auth/verify-email",
                new VerifyEmailRequestDto { Token = token });

            // STEP-02: Assert response.StatusCode is expectedStatus and AssertErrorEnvelope succeeds.
            response.StatusCode.Should().Be(expectedStatus);
            await AssertErrorEnvelope(response, expectedCode);
        }

        // TEST-05: Map every documented AUTH-09 token failure to its HTTP envelope.
        /// <summary>Verifies reset-token failures preserve the AUTH-09 status and error code.</summary>
        [Theory]
        [InlineData("invalid-token", "TOKEN_INVALID")]
        [InlineData("expired-token", "TOKEN_EXPIRED")]
        public async Task ResetPassword_WithApplicationFailure_ShouldReturnDocumentedEnvelope(
            string token,
            string expectedCode)
        {
            // STEP-01: Send POST /api/v1/auth/reset-password with a ResetPasswordRequestDto body and keep response.
            using var response = await _factory.CreateClient().PostAsJsonAsync(
                "/api/v1/auth/reset-password",
                new ResetPasswordRequestDto { Token = token, NewPassword = "Password2" });

            // STEP-02: Assert response.StatusCode is HttpStatusCode.BadRequest and AssertErrorEnvelope succeeds.
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await AssertErrorEnvelope(response, expectedCode);
        }

        // TEST-06: Map email-provider failure to the documented 503 envelope.
        /// <summary>Verifies an email delivery failure is wrapped as SERVICE_UNAVAILABLE.</summary>
        [Fact]
        public async Task ResendVerificationEmail_WhenEmailProviderFails_ShouldReturnServiceUnavailableEnvelope()
        {
            // STEP-01: Send POST /api/v1/auth/verify-email/resend with a ResendVerificationEmailRequestDto body and keep response.
            using var response = await _factory.CreateClient().PostAsJsonAsync(
                "/api/v1/auth/verify-email/resend",
                new ResendVerificationEmailRequestDto { Email = "service-unavailable@angi.test" });

            // STEP-02: Assert response.StatusCode is HttpStatusCode.ServiceUnavailable and AssertErrorEnvelope confirms SERVICE_UNAVAILABLE.
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            await AssertErrorEnvelope(response, "SERVICE_UNAVAILABLE");
        }

        // TEST-07: Return 429, Retry-After, and the canonical envelope on a repeated sensitive request.
        /// <summary>Verifies AUTH-03 and AUTH-08 share the documented per-email fixed-window limit.</summary>
        [Theory]
        [InlineData("/api/v1/auth/verify-email/resend")]
        [InlineData("/api/v1/auth/forgot-password")]
        public async Task SensitiveEmailEndpoint_TwiceWithinWindow_ShouldReturnRateLimitEnvelope(string path)
        {
            // STEP-01: Create the AuthenticationApiFactory under test as factory.
            await using var factory = new AuthenticationApiFactory(
                sensitiveWindowSeconds: 60,
                sensitivePermitLimit: 1);
            // STEP-02: Define email as "rate-{Guid.NewGuid()}@angi.test".
            var email = $"rate-{Guid.NewGuid():N}@angi.test";
            // STEP-03: Create the HTTP client.
            var client = factory.CreateClient();

            // STEP-04: Send POST path with a body of Email and keep first.
            using var first = await client.PostAsJsonAsync(path, new { Email = email });
            // STEP-05: Send POST path with a body of Email and keep second.
            using var second = await client.PostAsJsonAsync(path, new { Email = email });

            // STEP-06: Assert first.StatusCode is HttpStatusCode.OK, second.StatusCode is HttpStatusCode.TooManyRequests, second.Headers.RetryAfter is not null and 3 more.
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            second.Headers.RetryAfter.Should().NotBeNull();
            second.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero);
            second.Headers.RetryAfter.Delta.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
            await AssertErrorEnvelope(second, "TOO_MANY_REQUESTS");
        }

        // TEST-08: Admit a sensitive email request after the configured fixed window replenishes.
        /// <summary>Verifies AUTH-03 and AUTH-08 replenish their permits after the configured fixed window.</summary>
        [Theory]
        [InlineData("/api/v1/auth/verify-email/resend")]
        [InlineData("/api/v1/auth/forgot-password")]
        public async Task SensitiveEmailEndpoint_AfterWindow_ShouldBeAllowedAgain(string path)
        {
            // STEP-01: Create the AuthenticationApiFactory under test as factory; create HTTP client client.
            await using var factory = new AuthenticationApiFactory(
                sensitiveWindowSeconds: 1,
                sensitivePermitLimit: 1);
            var client = factory.CreateClient();
            // STEP-02: Define email as "reset-{Guid.NewGuid()}@angi.test".
            var email = $"reset-{Guid.NewGuid():N}@angi.test";

            // STEP-03: Send POST path with a body of Email and keep first.
            using var first = await client.PostAsJsonAsync(path, new { Email = email });
            // STEP-04: Send POST path with a body of Email and keep limited.
            using var limited = await client.PostAsJsonAsync(path, new { Email = email });
            // STEP-05: Call Task.Delay.
            await Task.Delay(TimeSpan.FromMilliseconds(2200));
            // STEP-06: Send POST path with a body of Email and keep replenished.
            using var replenished = await client.PostAsJsonAsync(path, new { Email = email });

            // STEP-07: Assert first.StatusCode is HttpStatusCode.OK, limited.StatusCode is HttpStatusCode.TooManyRequests and replenished.StatusCode is HttpStatusCode.OK.
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            replenished.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // TEST-09: Expose implemented Auth request and success schemas to API Explorer/OpenAPI.
        /// <summary>Verifies Scalar can discover each implemented Auth request and success schema.</summary>
        [Fact]
        public void ApiExplorer_ShouldExposeAuthenticationVerificationSchemas()
        {
            // STEP-01: Get descriptions from Items.SelectMany(filtered).ToArray.
            var descriptions = _factory.Services
                .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
                .ApiDescriptionGroups.Items
                .SelectMany(group => group.Items)
                .ToArray();

            // STEP-02: Assert AssertOpenApiContract confirms api/v1/auth/verify-email, AssertOpenApiContract confirms api/v1/auth/verify-email/resend and 3 more.
            AssertOpenApiContract<VerifyEmailRequestDto, VerifyEmailResponseDto>(
                descriptions,
                "api/v1/auth/verify-email");
            AssertOpenApiContract<ResendVerificationEmailRequestDto, object>(
                descriptions,
                "api/v1/auth/verify-email/resend");
            AssertOpenApiContract<ForgotPasswordRequestDto, object>(
                descriptions,
                "api/v1/auth/forgot-password");
            AssertOpenApiContract<ResetPasswordRequestDto, object>(
                descriptions,
                "api/v1/auth/reset-password");
            AssertOpenApiContract<GoogleLoginRequestDto, GoogleLoginResponseDto>(
                descriptions,
                "api/v1/auth/google");
        }

        // TEST-10: Map both AUTH-05 application errors to their documented HTTP envelopes.
        /// <summary>Verifies Google token and first-login role failures preserve their status and error code.</summary>
        [Theory]
        [InlineData("invalid-token", "TRAVELER", HttpStatusCode.Unauthorized, "GOOGLE_TOKEN_INVALID")]
        [InlineData("role-required", null, HttpStatusCode.BadRequest, "ROLE_REQUIRED")]
        public async Task GoogleLogin_WithApplicationFailure_ShouldReturnDocumentedEnvelope(
            string idToken,
            string? role,
            HttpStatusCode expectedStatus,
            string expectedCode)
        {
            // STEP-01: Send POST /api/v1/auth/google with a GoogleLoginRequestDto body and keep response.
            using var response = await _factory.CreateClient().PostAsJsonAsync(
                "/api/v1/auth/google",
                new GoogleLoginRequestDto { IdToken = idToken, Role = role });

            // STEP-02: Assert response.StatusCode is expectedStatus and AssertErrorEnvelope succeeds.
            response.StatusCode.Should().Be(expectedStatus);
            await AssertErrorEnvelope(response, expectedCode);
        }

        /// <summary>Asserts one POST endpoint publishes its body and successful response model.</summary>
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

        /// <summary>Asserts the canonical API error envelope and its optional field errors.</summary>
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

        /// <summary>Creates a host with permissive test rate limits.</summary>
        public AuthenticationApiFactory()
            : this(60, 100)
        {
        }

        /// <summary>Creates a host with deterministic sensitive-auth rate-limit settings.</summary>
        internal AuthenticationApiFactory(int sensitiveWindowSeconds, int sensitivePermitLimit)
        {
            _sensitiveWindowSeconds = sensitiveWindowSeconds;
            _sensitivePermitLimit = sensitivePermitLimit;
        }

        /// <summary>Supplies startup environment variables while the in-memory host is created.</summary>
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

        /// <summary>Replaces external and database Auth dependencies with deterministic stubs.</summary>
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
                services.RemoveAll<IForgotPasswordUseCase>();
                services.RemoveAll<IResetPasswordUseCase>();
                services.RemoveAll<IGoogleLoginUseCase>();
                services.AddScoped<IVerifyEmailUseCase, StubVerifyEmailUseCase>();
                services.AddScoped<IResendVerificationEmailUseCase, StubResendVerificationEmailUseCase>();
                services.AddScoped<IForgotPasswordUseCase, StubForgotPasswordUseCase>();
                services.AddScoped<IResetPasswordUseCase, StubResetPasswordUseCase>();
                services.AddScoped<IGoogleLoginUseCase, StubGoogleLoginUseCase>();
            });
        }

        /// <summary>AUTH-02 stub that returns a fixed outcome for each test token.</summary>
        private sealed class StubVerifyEmailUseCase : IVerifyEmailUseCase
        {
            /// <summary>Returns deterministic AUTH-02 outcomes for HTTP error mapping tests.</summary>
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

        /// <summary>AUTH-03 stub that returns success or an email delivery failure.</summary>
        private sealed class StubResendVerificationEmailUseCase : IResendVerificationEmailUseCase
        {
            /// <summary>Returns deterministic AUTH-03 success and delivery-failure outcomes.</summary>
            public Task ExecuteAsync(ResendVerificationEmailRequestDto request, CancellationToken ct)
            {
                if (request.Email.Equals("service-unavailable@angi.test", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ServiceUnavailableException("SERVICE_UNAVAILABLE", "Không gửi được email.");
                }

                return Task.CompletedTask;
            }
        }

        /// <summary>AUTH-08 stub that always succeeds without email or database access.</summary>
        private sealed class StubForgotPasswordUseCase : IForgotPasswordUseCase
        {
            /// <summary>Returns a deterministic AUTH-08 success without contacting email or PostgreSQL.</summary>
            public Task ExecuteAsync(ForgotPasswordRequestDto request, CancellationToken ct) => Task.CompletedTask;
        }

        /// <summary>AUTH-09 stub that returns a fixed outcome for each test token.</summary>
        private sealed class StubResetPasswordUseCase : IResetPasswordUseCase
        {
            /// <summary>Returns deterministic AUTH-09 outcomes for HTTP error mapping tests.</summary>
            public Task ExecuteAsync(ResetPasswordRequestDto request, CancellationToken ct)
            {
                return request.Token switch
                {
                    "invalid-token" => throw new BadRequestException("TOKEN_INVALID", "Token không hợp lệ."),
                    "expired-token" => throw new BadRequestException("TOKEN_EXPIRED", "Token đã hết hạn."),
                    _ => Task.CompletedTask
                };
            }
        }

        /// <summary>AUTH-05 stub that returns a fixed outcome for each test token without calling Google.</summary>
        private sealed class StubGoogleLoginUseCase : IGoogleLoginUseCase
        {
            /// <summary>Returns deterministic AUTH-05 outcomes without contacting Google or PostgreSQL.</summary>
            public Task<GoogleLoginResponseDto> ExecuteAsync(
                GoogleLoginRequestDto request,
                CancellationToken ct)
            {
                return request.IdToken switch
                {
                    "invalid-token" => throw new UnauthorizedException(
                        "GOOGLE_TOKEN_INVALID",
                        "Google ID token không hợp lệ."),
                    "role-required" => throw new BadRequestException(
                        "ROLE_REQUIRED",
                        "Vai trò là bắt buộc."),
                    _ => Task.FromResult(new GoogleLoginResponseDto
                    {
                        AccessToken = "access-token",
                        IsNewUser = false
                    })
                };
            }
        }
    }
}
