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

namespace ANGI.IntegrationTests.WebApi.Controllers.Restaurant;

/// <summary>Verifies the owner menu endpoints' auth, body handling and DI wiring through the real HTTP pipeline.</summary>
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
        // STEP-01: Get request from Request.
        using var request = Request(method, path, body);

        // STEP-02: Send request through the HTTP client and keep response.
        using var response = await _factory.CreateClient().SendAsync(request);

        // STEP-03: Assert response.StatusCode is HttpStatusCode.Unauthorized and AssertErrorEnvelope confirms UNAUTHORIZED.
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
        // STEP-01: Get request from Request and attach the bearer token to request.
        using var request = Request(method, path, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("USER"));

        // STEP-02: Send request through the HTTP client and keep response.
        using var response = await _factory.CreateClient().SendAsync(request);

        // STEP-03: Assert response.StatusCode is HttpStatusCode.Forbidden and AssertErrorEnvelope confirms FORBIDDEN.
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
        // STEP-01: Build the HTTP request and attach the bearer token to request.
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("RESTAURANT_OWNER"));

        // STEP-02: Send request through the HTTP client and keep response.
        using var response = await _factory.CreateClient().SendAsync(request);

        // STEP-03: Read document from the response; assert response.StatusCode is HttpStatusCode.BadRequest, AssertErrorEnvelope confirms VALIDATION_FAILED and 2 more.
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
        // STEP-01: Get request from Request and attach the bearer token to request.
        using var request = Request(new HttpMethod(method), path, "{");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("RESTAURANT_OWNER"));

        // STEP-02: Send request through the HTTP client and keep response.
        using var response = await _factory.CreateClient().SendAsync(request);

        // STEP-03: Assert response.StatusCode is HttpStatusCode.BadRequest and AssertErrorEnvelope confirms VALIDATION_FAILED.
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
        // STEP-01: Create scope through _factory.Services.CreateScope, derive provider from scope.ServiceProvider and 1 more.
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var controllerDependencies = typeof(OwnerMenuController).GetConstructors().Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType);

        // STEP-02: Assert controllerDependencies.Select(filtered) not contain nulls and provider.GetRequiredService() is not null.
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
        // STEP-01: Get actions from Where(filtered).Where(filtered).ToArray.
        var actions = typeof(OwnerMenuController).GetMethods()
            .Where(method => method.DeclaringType == typeof(OwnerMenuController))
            .Where(method => method.GetCustomAttributes(typeof(HttpMethodAttribute), true).Length > 0)
            .ToArray();

        // STEP-02: Assert actions has 10 item(s) and actions only holds items matching the condition.
        actions.Should().HaveCount(10);
        actions.Should().OnlyContain(method =>
            method.GetCustomAttributes(typeof(ProducesResponseTypeAttribute), true).Length > 0);
    }

    // TEST-07: Publish all ten controller operations through the runtime endpoint data source.
    /// <summary>Verifies that ASP.NET Core publishes every owner-menu action and response schema.</summary>
    [Fact]
    public void RuntimeEndpointDataSource_ShouldExposeAllTenOwnerMenuOperations()
    {
        // STEP-01: Get endpoints from Endpoints.Where(filtered).ToArray.
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?
                .ControllerTypeInfo.AsType() == typeof(OwnerMenuController))
            .ToArray();

        // STEP-02: Assert endpoints has 10 item(s) and endpoints only holds items matching the condition.
        endpoints.Should().HaveCount(10);
        endpoints.Should().OnlyContain(endpoint =>
            endpoint.Metadata.GetMetadata<ProducesResponseTypeAttribute>() != null);
    }

    // TEST-08: Return the real HTTP success envelope with pending state, note, and UTC timestamp.
    /// <summary>Verifies OWN-17 serialization through ASP.NET Core rather than controller invocation.</summary>
    [Fact]
    public async Task SubmitEndpoint_WithValidRequest_ShouldSerializePendingSubmission()
    {
        // STEP-01: Build a POST /api/v1/owner/restaurant/menu-submissions/14/submit request with a JSON body and attach the bearer token to request.
        using var request = Request(
            HttpMethod.Post,
            "/api/v1/owner/restaurant/menu-submissions/14/submit",
            "{\"ownerNote\":\"Gửi duyệt\"}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Token("RESTAURANT_OWNER"));

        // STEP-02: Send request through the HTTP client and keep response.
        using var response = await _factory.CreateClient().SendAsync(request);

        // STEP-03: Read document from the response; assert response.StatusCode is HttpStatusCode.OK.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // STEP-04: Derive root from document.RootElement.
        var root = document.RootElement;
        // STEP-05: Assert root.success is true.
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        // STEP-06: Get data from root.GetProperty.
        var data = root.GetProperty("data");
        // STEP-07: Assert data.id is 14, data.status is "pending", data.ownerNote is the expected text and data.submittedAt is "2026-10-18T09:30:00Z".
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
        // STEP-01: Send GET /openapi/v1.json and keep response.
        using var response = await _factory.CreateClient().GetAsync("/openapi/v1.json");

        // STEP-02: Read document from the response; assert response.StatusCode is HttpStatusCode.OK.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // STEP-03: Get operation from GetProperty.
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/owner/restaurant/menu-submissions/{submissionId}/submit")
            .GetProperty("post");
        // STEP-04: Assert operation.requestBody.content.TryGetProperty() is true and operation.responses.TryGetProperty() is true.
        operation.GetProperty("requestBody").GetProperty("content")
            .TryGetProperty("application/json", out _).Should().BeTrue();
        operation.GetProperty("responses").TryGetProperty("200", out _).Should().BeTrue();

        // STEP-05: Get schemas from document.RootElement.components.GetProperty and get requestSchema from EnumerateObject().Select(filtered).Single.
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var requestSchema = schemas.EnumerateObject()
            .Select(schema => schema.Value)
            .Single(schema =>
                schema.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty("ownerNote", out var ownerNote) &&
                ownerNote.TryGetProperty("maxLength", out _));
        // STEP-06: Assert requestSchema.properties.ownerNote.maxLength is 1000 and schemas.EnumerateObject().Any(filtered) is true.
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
        // STEP-01: Send GET /scalar/v1 and keep response.
        using var response = await _factory.CreateClient().GetAsync("/scalar/v1");

        // STEP-02: Assert response.StatusCode is HttpStatusCode.OK and the response body contains "openapi/v1.json".
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

    /// <summary>Account check that always passes, so contract tests need no database.</summary>
    internal sealed class NoOpAccountCheck : IEnsureActiveAccountUseCase
    {
        /// <summary>Allows HTTP contract tests to bypass database-backed account status checks.</summary>
        public Task ExecuteAsync(int userId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>OWN-17 stub that returns a sample submitted menu.</summary>
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

/// <summary>Test host with JWT settings and stubs for the owner menu endpoints.</summary>
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
