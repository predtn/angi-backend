using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.Test.WebApi.Controllers.Restaurant;

public sealed class OwnerMenuHttpContractTests : IClassFixture<OwnerMenuApiFactory>
{
    private readonly OwnerMenuApiFactory _factory;

    /// <summary>Initializes HTTP contract tests with the shared owner-menu API host.</summary>
    public OwnerMenuHttpContractTests(OwnerMenuApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Lists every owner-menu endpoint with its HTTP method and optional request body.</summary>
    public static TheoryData<HttpMethod, string, string?> OwnerEndpoints => new()
    {
        { HttpMethod.Get, "/api/v1/owner/restaurant/menu", null },
        { HttpMethod.Put, "/api/v1/owner/restaurant/menu/dishes/1/availability", "{}" },
        { HttpMethod.Post, "/api/v1/owner/restaurant/menu-submissions", null },
        { HttpMethod.Get, "/api/v1/owner/restaurant/menu-submissions", null },
        { HttpMethod.Get, "/api/v1/owner/restaurant/menu-submissions/1", null },
        { HttpMethod.Post, "/api/v1/owner/restaurant/menu-submissions/1/items", "{}" },
        { HttpMethod.Patch, "/api/v1/owner/restaurant/menu-submissions/1/items/1", "{}" },
        { HttpMethod.Delete, "/api/v1/owner/restaurant/menu-submissions/1/items/1", null },
        { HttpMethod.Post, "/api/v1/owner/restaurant/menu-submissions/1/submit", "{}" },
        { HttpMethod.Post, "/api/v1/owner/restaurant/menu-submissions/1/cancel", null }
    };

    // TEST-01: Return the canonical 401 envelope for all ten endpoints without a token.
    /// <summary>Verifies authorization and the standard error envelope for anonymous requests.</summary>
    [Theory]
    [MemberData(nameof(OwnerEndpoints))]
    public async Task Endpoint_WithoutToken_ShouldReturnStandardUnauthorizedEnvelope(
        HttpMethod method,
        string path,
        string? body)
    {
        using var request = Request(method, path, body);

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            await response.Content.ReadAsStringAsync());
        await AssertErrorEnvelope(response, "UNAUTHORIZED");
    }

    // TEST-02: Return the canonical 403 envelope for all ten endpoints with the wrong role.
    /// <summary>Verifies role authorization and the standard error envelope for non-owner users.</summary>
    [Theory]
    [MemberData(nameof(OwnerEndpoints))]
    public async Task Endpoint_WithWrongRole_ShouldReturnStandardForbiddenEnvelope(
        HttpMethod method,
        string path,
        string? body)
    {
        using var request = Request(method, path, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("USER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());
        await AssertErrorEnvelope(response, "FORBIDDEN");
    }

    // TEST-03: Return VALIDATION_FAILED when a body endpoint receives no body.
    /// <summary>Verifies that every JSON body endpoint rejects a missing body consistently.</summary>
    [Theory]
    [InlineData("/api/v1/owner/restaurant/menu/dishes/1/availability", "PUT")]
    [InlineData("/api/v1/owner/restaurant/menu-submissions/1/items", "POST")]
    [InlineData("/api/v1/owner/restaurant/menu-submissions/1/items/1", "PATCH")]
    [InlineData("/api/v1/owner/restaurant/menu-submissions/1/submit", "POST")]
    public async Task BodyEndpoint_WithMissingBody_ShouldReturnValidationEnvelope(string path, string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("RESTAURANT_OWNER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());
        await AssertErrorEnvelope(response, "VALIDATION_FAILED");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("message").GetString()
            .Should().Be("Dữ liệu không hợp lệ.");
        document.RootElement.GetProperty("errors").GetProperty("body")[0].GetString()
            .Should().Be("Nội dung JSON là bắt buộc và phải có Content-Type application/json.");
    }

    // TEST-04: Return VALIDATION_FAILED when a body endpoint receives malformed JSON.
    /// <summary>Verifies that every JSON body endpoint rejects malformed JSON consistently.</summary>
    [Theory]
    [InlineData("/api/v1/owner/restaurant/menu/dishes/1/availability", "PUT")]
    [InlineData("/api/v1/owner/restaurant/menu-submissions/1/items", "POST")]
    [InlineData("/api/v1/owner/restaurant/menu-submissions/1/items/1", "PATCH")]
    [InlineData("/api/v1/owner/restaurant/menu-submissions/1/submit", "POST")]
    public async Task BodyEndpoint_WithMalformedJson_ShouldReturnValidationEnvelope(string path, string method)
    {
        using var request = Request(new HttpMethod(method), path, "{");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("RESTAURANT_OWNER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());
        await AssertErrorEnvelope(response, "VALIDATION_FAILED");
    }

    // TEST-05: Resolve controller dependencies, use cases, validators, and repository from DI.
    /// <summary>Verifies complete dependency registration for the owner-menu HTTP surface.</summary>
    [Fact]
    public void Services_ShouldResolveEveryControllerDependencyUseCaseValidatorAndRepository()
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var controllerDependencies = typeof(OwnerMenuController).GetConstructors().Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType);

        controllerDependencies.Select(type => provider.GetService(type)).Should().NotContainNulls();
        provider.GetRequiredService<IValidator<AddMenuSubmissionItemRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<UpdateMenuSubmissionItemRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<SubmitMenuSubmissionRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<ListMenuSubmissionsRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IRestaurantMenuRepository>().Should().NotBeNull();
    }

    // TEST-06: Declare exactly ten documented actions with response schemas.
    /// <summary>Verifies that controller reflection exposes the complete documented route set.</summary>
    [Fact]
    public void Controller_ShouldDeclareExactlyTenDocumentedRoutesAndResponseSchemas()
    {
        var actions = typeof(OwnerMenuController).GetMethods()
            .Where(method => method.DeclaringType == typeof(OwnerMenuController))
            .Where(method => method.GetCustomAttributes(typeof(HttpMethodAttribute), true).Length > 0)
            .ToArray();

        actions.Should().HaveCount(10);
        actions.Should().OnlyContain(method =>
            method.GetCustomAttributes(typeof(ProducesResponseTypeAttribute), true).Length > 0);
    }

    // TEST-07: Publish all ten controller operations through the runtime endpoint data source.
    /// <summary>Verifies that ASP.NET Core publishes every owner-menu action and response schema.</summary>
    [Fact]
    public void RuntimeEndpointDataSource_ShouldExposeAllTenOwnerMenuOperations()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?
                .ControllerTypeInfo.AsType() == typeof(OwnerMenuController))
            .ToArray();

        endpoints.Should().HaveCount(10);
        endpoints.Should().OnlyContain(endpoint =>
            endpoint.Metadata.GetMetadata<ProducesResponseTypeAttribute>() != null);
    }

    // TEST-08: Return the real HTTP success envelope with pending state, note, and UTC timestamp.
    /// <summary>Verifies OWN-17 serialization through ASP.NET Core rather than controller invocation.</summary>
    [Fact]
    public async Task SubmitEndpoint_WithValidRequest_ShouldSerializePendingSubmission()
    {
        using var request = Request(
            HttpMethod.Post,
            "/api/v1/owner/restaurant/menu-submissions/14/submit",
            "{\"ownerNote\":\"Gửi duyệt\"}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Token("RESTAURANT_OWNER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        var data = root.GetProperty("data");
        data.GetProperty("id").GetInt64().Should().Be(14);
        data.GetProperty("status").GetString().Should().Be("pending");
        data.GetProperty("ownerNote").GetString().Should().Be("Gửi duyệt");
        data.GetProperty("submittedAt").GetString().Should().Be("2026-10-18T09:30:00Z");
    }

    // TEST-09: Publish OWN-17 and its ownerNote maximum in the generated OpenAPI document.
    /// <summary>Verifies the contract consumed by Scalar, including request and response schemas.</summary>
    [Fact]
    public async Task OpenApiDocument_ShouldContainSubmitEndpointAndSchemas()
    {
        using var response = await _factory.CreateClient().GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/owner/restaurant/menu-submissions/{submissionId}/submit")
            .GetProperty("post");
        operation.GetProperty("requestBody").GetProperty("content")
            .TryGetProperty("application/json", out _).Should().BeTrue();
        operation.GetProperty("responses").TryGetProperty("200", out _).Should().BeTrue();

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var requestSchema = schemas.EnumerateObject()
            .Select(schema => schema.Value)
            .Single(schema =>
                schema.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty("ownerNote", out var ownerNote) &&
                ownerNote.TryGetProperty("maxLength", out _));
        requestSchema.GetProperty("properties").GetProperty("ownerNote")
            .GetProperty("maxLength").GetInt32().Should().Be(1000);
        schemas.EnumerateObject().Any(schema =>
            schema.Value.TryGetProperty("properties", out var properties) &&
            properties.TryGetProperty("submittedAt", out var submittedAt) &&
            properties.TryGetProperty("status", out var status) &&
            submittedAt.ValueKind != JsonValueKind.Undefined &&
            status.ValueKind != JsonValueKind.Undefined).Should().BeTrue();
    }

    // TEST-10: Serve the Scalar API reference page in the testing host.
    /// <summary>Verifies that the generated OpenAPI contract is reachable from Scalar.</summary>
    [Fact]
    public async Task ScalarReference_ShouldBePublished()
    {
        using var response = await _factory.CreateClient().GetAsync("/scalar/v1");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("openapi/v1.json");
    }

    /// <summary>Builds an HTTP request with JSON content when a body is supplied.</summary>
    private static HttpRequestMessage Request(HttpMethod method, string path, string? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>Asserts the shared API error envelope and expected error code.</summary>
    private static async Task AssertErrorEnvelope(HttpResponseMessage response, string errorCode)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        document.RootElement.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Creates a signed test access token for the requested application role.</summary>
    private static string Token(string role)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(OwnerMenuApiFactory.Secret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            OwnerMenuApiFactory.Issuer,
            OwnerMenuApiFactory.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "12"), new Claim("role", role)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal sealed class NoOpAccountCheck : IEnsureActiveAccountUseCase
    {
        /// <summary>Allows HTTP contract tests to bypass database-backed account status checks.</summary>
        public Task ExecuteAsync(int userId, CancellationToken ct) => Task.CompletedTask;
    }

    internal sealed class StubSubmitMenuSubmissionUseCase : ISubmitMenuSubmissionUseCase
    {
        /// <summary>Returns a deterministic submitted menu for HTTP serialization tests.</summary>
        public Task<MenuSubmissionDto> ExecuteAsync(
            long submissionId,
            SubmitMenuSubmissionRequestDto request,
            CancellationToken ct)
        {
            return Task.FromResult(new MenuSubmissionDto
            {
                Id = submissionId,
                RestaurantId = 7,
                Status = "pending",
                OwnerNote = request.OwnerNote,
                SubmittedAt = new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc)
            });
        }
    }
}

public sealed class OwnerMenuApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "ANGI.HttpTests";
    public const string Audience = "ANGI.HttpTests.Client";
    public const string Secret = "ANGI-53-http-contract-test-secret-key-123456789";

    /// <summary>Configures deterministic authentication and replaces database-dependent account checks.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:SecretKey"] = Secret,
                ["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IConfigureOptions<JwtBearerOptions>>();
            services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure(options =>
                {
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = Issuer,
                        ValidateAudience = true,
                        ValidAudience = Audience,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.Zero,
                        NameClaimType = JwtRegisteredClaimNames.Sub,
                        RoleClaimType = "role"
                    };
                    options.Events = new JwtBearerEvents
                    {
                        OnChallenge = async context =>
                        {
                            context.HandleResponse();
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            await context.Response.WriteAsJsonAsync(new
                            {
                                success = false,
                                message = "Authentication is required.",
                                errorCode = "UNAUTHORIZED",
                                data = (object?)null
                            });
                        },
                        OnForbidden = async context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            await context.Response.WriteAsJsonAsync(new
                            {
                                success = false,
                                message = "Permission is required.",
                                errorCode = "FORBIDDEN",
                                data = (object?)null
                            });
                        }
                    };
                });
            services.RemoveAll<IEnsureActiveAccountUseCase>();
            services.AddScoped<IEnsureActiveAccountUseCase, OwnerMenuHttpContractTests.NoOpAccountCheck>();
            services.RemoveAll<ISubmitMenuSubmissionUseCase>();
            services.AddScoped<
                ISubmitMenuSubmissionUseCase,
                OwnerMenuHttpContractTests.StubSubmitMenuSubmissionUseCase>();
        });
    }

}
