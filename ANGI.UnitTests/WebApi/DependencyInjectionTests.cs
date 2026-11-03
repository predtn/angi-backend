using System.Reflection;
using ANGI.Application;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
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

namespace ANGI.UnitTests.WebApi
{
    /// <summary>Verifies WebApi service registration, configured HTTP contracts, and bound request validation.</summary>
    public class DependencyInjectionTests
    {
        private static readonly NullabilityInfoContext Nullability = new();

        // TEST-01: Verify that JWT validation and claim mapping are configured correctly.
        /// <summary>Verifies JWT validation uses the configured issuer, audience, key, lifetime, and claims.</summary>
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
        /// <summary>Verifies the Frontend CORS policy permits only configured origins.</summary>
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
            policy.ExposedHeaders.Should().Contain("Retry-After");
            policy.SupportsCredentials.Should().BeFalse();
        }

        // TEST-03: Verify that the rate limiter no longer counts login attempts; only failed logins are
        // limited, by LoginFailureLimitMiddleware (see LoginFailureLimitMiddlewareTests).
        /// <summary>Verifies successful login traffic is not constrained by the failed-login threshold.</summary>
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

        // TEST-04: Share the resend rate-limit partition for the same email across IP addresses.
        /// <summary>Verifies AUTH-03 is limited once per email rather than once per email and IP pair.</summary>
        [Fact]
        public async Task RateLimiter_ShouldLimitVerificationResendByEmailAcrossIpAddresses()
        {
            using var serviceProvider = BuildServiceProvider();
            var options = serviceProvider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
            var firstContext = CreateResendContext("user@angi.test", "127.0.0.1");
            var secondContext = CreateResendContext("user@angi.test", "127.0.0.2");

            using var firstLease = await options.GlobalLimiter!.AcquireAsync(firstContext);
            using var secondLease = await options.GlobalLimiter.AcquireAsync(secondContext);

            firstLease.IsAcquired.Should().BeTrue();
            secondLease.IsAcquired.Should().BeFalse();
        }

        // TEST-05: Keep the sensitive authentication window at the documented 60 seconds.
        /// <summary>Verifies the reset behavior exercised with an accelerated HTTP test is configured as 60 seconds in production.</summary>
        [Fact]
        public void RateLimitConfiguration_ShouldUseDocumentedSensitiveWindow()
        {
            using var serviceProvider = BuildServiceProvider();

            var settings = serviceProvider.GetRequiredService<IOptions<RateLimitSettings>>().Value;

            settings.SensitiveAuthPermitLimit.Should().Be(1);
            settings.SensitiveAuthWindowSeconds.Should().Be(60);
        }

        /// <summary>Creates an AUTH-03 context carrying the normalized rate-limit email and selected IP.</summary>
        private static DefaultHttpContext CreateResendContext(string email, string ipAddress)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = "/api/v1/auth/verify-email/resend";
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ipAddress);
            context.Items[RateLimitPartitionMiddleware.EmailItemKey] = email;
            return context;
        }

        /// <summary>Builds a WebApi service provider using deterministic test configuration.</summary>
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
        /// <summary>Lists every non-nullable reference property of every Application request DTO a controller binds.</summary>
        public static TheoryData<Type, string> NonNullableRequestProperties()
        {
            var data = new TheoryData<Type, string>();
            foreach (var type in RequestDtoTypes().OrderBy(type => type.FullName))
            {
                foreach (var property in NonNullableReferenceProperties(type))
                {
                    data.Add(type, property.Name);
                }
            }

            return data;
        }

        // TEST-06: Keep implicit [Required] off, so required-field messages come from the validators, in Vietnamese.
        /// <summary>Verifies the MVC option is set by AddWebApi.</summary>
        [Fact]
        public void AddWebApi_ShouldSuppressImplicitRequiredForNonNullableReferences()
        {
            var services = new ServiceCollection();
            services.AddWebApi(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

            var options = services.BuildServiceProvider()
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<MvcOptions>>().Value;

            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes.Should().BeTrue();
        }

        // TEST-07: Every non-nullable reference property of a request DTO is reported by its validator, once, when null.
        /// <summary>Sets one property to null and expects exactly one validation error on that property.</summary>
        [Theory]
        [MemberData(nameof(NonNullableRequestProperties))]
        public async Task Validator_WithNullNonNullableProperty_ShouldReportIt(Type dtoType, string propertyName)
        {
            using var provider = new ServiceCollection().AddApplication().BuildServiceProvider();
            var validator = (IValidator?)provider.GetService(typeof(IValidator<>).MakeGenericType(dtoType));
            validator.Should().NotBeNull($"{dtoType.Name} has non-nullable reference properties, so it needs a validator");

            var instance = Activator.CreateInstance(dtoType)!;
            dtoType.GetProperty(propertyName)!.SetValue(instance, null);
            var context = (IValidationContext)Activator.CreateInstance(
                typeof(ValidationContext<>).MakeGenericType(dtoType), instance)!;

            var result = await validator!.ValidateAsync(context);

            result.Errors.Where(error => error.PropertyName == propertyName).Should().ContainSingle(
                $"{dtoType.Name}.{propertyName} = null must be rejected once, before the use case runs");
        }

        /// <summary>Request DTOs bound by a controller action, with the DTOs nested inside them.</summary>
        private static IEnumerable<Type> RequestDtoTypes()
        {
            var found = new HashSet<Type>();
            var actionParameters = typeof(ANGI.WebApi.DependencyInjection).Assembly.GetTypes()
                .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .SelectMany(method => method.GetParameters());
            foreach (var parameter in actionParameters)
            {
                Collect(parameter.ParameterType, found);
            }

            return found;
        }

        /// <summary>Collects bound request DTOs and their nested request types once.</summary>
        private static void Collect(Type type, HashSet<Type> found)
        {
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Collect(argument, found);
                }

                return;
            }

            if (!type.IsClass || type == typeof(string) ||
                type.Namespace?.StartsWith("ANGI.Application.DTOs", StringComparison.Ordinal) != true ||
                !found.Add(type))
            {
                return;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Collect(property.PropertyType, found);
            }
        }

        /// <summary>Finds writable non-nullable reference properties whose validation must reject null.</summary>
        private static IEnumerable<PropertyInfo> NonNullableReferenceProperties(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanWrite && property.SetMethod!.IsPublic && !property.PropertyType.IsValueType)
                .Where(property => Nullability.Create(property).WriteState == NullabilityState.NotNull);
    }
}
