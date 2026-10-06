using System.Text;
using ANGI.WebApi;
using ANGI.WebApi.Configs;
using ANGI.WebApi.Middlewares;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ANGI.Test.WebApi.Configs
{
    public class WebApiConfigurationTests
    {
        // TEST-01: Verify that JWT validation and claim mapping are configured correctly.
        [Fact]
        public void AddWebApi_ShouldConfigureJwtValidationAndClaimMapping()
        {
            using var serviceProvider = BuildServiceProvider();

            var options = serviceProvider
                .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme);

            options.MapInboundClaims.Should().BeFalse();
            options.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
            options.TokenValidationParameters.ValidIssuer.Should().Be("ANGI.WebApi.Tests");
            options.TokenValidationParameters.ValidateAudience.Should().BeTrue();
            options.TokenValidationParameters.ValidAudience.Should().Be("ANGI.Client.Tests");
            options.TokenValidationParameters.ValidateIssuerSigningKey.Should().BeTrue();
            options.TokenValidationParameters.ValidateLifetime.Should().BeTrue();
            options.TokenValidationParameters.ClockSkew.Should().Be(TimeSpan.Zero);
            options.TokenValidationParameters.NameClaimType.Should().Be("sub");
            options.TokenValidationParameters.RoleClaimType.Should().Be("role");
        }

        // TEST-02: Verify that CORS allows only the configured origins.
        [Fact]
        public async Task AddWebApi_ShouldConfigureOnlyAllowedCorsOrigins()
        {
            using var serviceProvider = BuildServiceProvider();
            var policyProvider = serviceProvider.GetRequiredService<ICorsPolicyProvider>();

            var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), CorsConfig.PolicyName);

            policy.Should().NotBeNull();
            policy!.Origins.Should().BeEquivalentTo("https://app.angi.test");
            policy.AllowAnyHeader.Should().BeTrue();
            policy.AllowAnyMethod.Should().BeTrue();
            policy.SupportsCredentials.Should().BeFalse();
        }

        // TEST-03: Verify that the rate limiter no longer counts login attempts; only failed logins are
        // limited, by LoginFailureLimitMiddleware (see LoginFailureLimitMiddlewareTests).
        [Fact]
        public async Task RateLimiter_ShouldNotLimitLoginByLoginPermitLimit()
        {
            using var serviceProvider = BuildServiceProvider();
            var options = serviceProvider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/api/v1/auth/login";
            httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            httpContext.Items[RateLimitPartitionMiddleware.EmailItemKey] = "user@angi.test";

            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var lease = await options.GlobalLimiter!.AcquireAsync(httpContext);
                lease.IsAcquired.Should().BeTrue();
            }
        }

        // TEST-04: Verify that the rate-limit middleware normalizes email and rewinds the request body.
        [Fact]
        public async Task RateLimitPartitionMiddleware_ShouldNormalizeEmailAndRewindBody()
        {
            const string requestJson = "{\"email\":\"  User@ANGI.Test  \",\"password\":\"secret\"}";
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/api/v1/auth/login";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
            var middleware = new RateLimitPartitionMiddleware();

            await middleware.InvokeAsync(context, async nextContext =>
            {
                nextContext.Items[RateLimitPartitionMiddleware.EmailItemKey]
                    .Should().Be("user@angi.test");

                using var reader = new StreamReader(
                    nextContext.Request.Body,
                    Encoding.UTF8,
                    leaveOpen: true);
                (await reader.ReadToEndAsync()).Should().Be(requestJson);
            });
        }

        private static ServiceProvider BuildServiceProvider()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Issuer"] = "ANGI.WebApi.Tests",
                    ["Jwt:Audience"] = "ANGI.Client.Tests",
                    ["Jwt:SecretKey"] = "test-secret-key-that-is-at-least-32-bytes-long",
                    ["Jwt:AccessTokenMinutes"] = "15",
                    ["Jwt:RefreshTokenDays"] = "30",
                    ["Cors:AllowedOrigins:0"] = "https://app.angi.test",
                    ["RateLimit:GeneralPermitLimit"] = "100",
                    ["RateLimit:GeneralWindowSeconds"] = "60",
                    ["RateLimit:LoginPermitLimit"] = "2",
                    ["RateLimit:LoginWindowMinutes"] = "15",
                    ["RateLimit:SensitiveAuthPermitLimit"] = "1",
                    ["RateLimit:SensitiveAuthWindowSeconds"] = "60"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWebApi(configuration);
            return services.BuildServiceProvider();
        }
    }
}
