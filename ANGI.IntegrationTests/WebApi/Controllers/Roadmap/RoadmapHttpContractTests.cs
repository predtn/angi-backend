using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Roadmap;
using ANGI.WebApi.Controllers.Roadmap;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.IntegrationTests.WebApi.Controllers.Roadmap;

public sealed class RoadmapHttpContractTests : IClassFixture<RoadmapApiFactory>
{
    private readonly RoadmapApiFactory _factory;

    /// <summary>Initializes HTTP contract tests with the shared roadmap API host.</summary>
    public RoadmapHttpContractTests(RoadmapApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Return the canonical 401 envelope for every implemented roadmap endpoint without a token.
    /// <summary>Verifies RM-01, RM-02, and RM-04 require authentication.</summary>
    [Theory]
    [InlineData("POST", "/api/v1/roadmaps")]
    [InlineData("GET", "/api/v1/roadmaps")]
    [InlineData("PATCH", "/api/v1/roadmaps/88")]
    public async Task Endpoint_WithoutToken_ShouldReturnUnauthorized(string method, string path)
    {
        using var request = JsonRequest(method, path, "{}");

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertErrorCode(response, "UNAUTHORIZED");
    }

    // TEST-02: Return the canonical 403 envelope when a non-traveler calls a roadmap endpoint.
    /// <summary>Verifies the controller enforces the documented TRAVELER role.</summary>
    [Theory]
    [InlineData("POST", "/api/v1/roadmaps")]
    [InlineData("GET", "/api/v1/roadmaps")]
    [InlineData("PATCH", "/api/v1/roadmaps/88")]
    public async Task Endpoint_WithWrongRole_ShouldReturnForbidden(string method, string path)
    {
        using var request = JsonRequest(method, path, "{}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("RESTAURANT_OWNER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertErrorCode(response, "FORBIDDEN");
    }

    // TEST-03: Return VALIDATION_FAILED for missing JSON bodies on both endpoints.
    /// <summary>Verifies the shared null-body filter covers the complete ANGI-104 HTTP surface.</summary>
    [Theory]
    [InlineData("POST", "/api/v1/roadmaps")]
    [InlineData("PATCH", "/api/v1/roadmaps/88")]
    public async Task Endpoint_WithMissingBody_ShouldReturnValidationEnvelope(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertErrorCode(response, "VALIDATION_FAILED");
    }

    // TEST-04: Serialize the complete RM-01 success envelope with HTTP 201.
    /// <summary>Verifies controller routing, request binding, use-case delegation, and JSON shape.</summary>
    [Fact]
    public async Task Create_WithValidBody_ShouldSerializeCreatedRoadmapDetail()
    {
        using var request = JsonRequest("POST", "/api/v1/roadmaps", """
            {"title":"Trip","numDays":3,"originMode":"none"}
            """);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("overview").GetProperty("id").GetInt64().Should().Be(88);
        data.GetProperty("itinerary").GetArrayLength().Should().Be(3);
        data.GetProperty("budget").GetProperty("totalEstimatedCost").GetDecimal().Should().Be(0);
    }

    // TEST-05: Preserve explicit null versus omission while binding the RM-04 request.
    /// <summary>Verifies PATCH model binding can explicitly clear nullable roadmap fields.</summary>
    [Fact]
    public async Task Update_WithExplicitNulls_ShouldSerializeUpdatedRoadmapDetail()
    {
        using var request = JsonRequest("PATCH", "/api/v1/roadmaps/88", """
            {"startDate":null,"budgetAmount":null,"originLabel":null}
            """);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.Update.LastRequest.Should().NotBeNull();
        _factory.Update.LastRequest!.StartDateSpecified.Should().BeTrue();
        _factory.Update.LastRequest.BudgetAmountSpecified.Should().BeTrue();
        _factory.Update.LastRequest.OriginLabelSpecified.Should().BeTrue();
    }

    // TEST-06: Bind RM-02 query parameters and serialize the complete paged response.
    /// <summary>Verifies routing, query binding, use-case delegation, and JSON shape for the roadmap list.</summary>
    [Fact]
    public async Task GetMyRoadmaps_WithValidQuery_ShouldSerializePagedRoadmapSummaries()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/roadmaps?page=2&pageSize=10");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("page").GetInt32().Should().Be(2);
        data.GetProperty("pageSize").GetInt32().Should().Be(10);
        data.GetProperty("totalItems").GetInt64().Should().Be(11);
        data.GetProperty("totalPages").GetInt32().Should().Be(2);
        data.GetProperty("items")[0].GetProperty("originMode").GetString().Should().Be("pinned");
        _factory.GetMyRoadmaps.LastRequest.Should().BeEquivalentTo(new { Page = 2, PageSize = 10 });
    }

    // TEST-07: Resolve every implemented roadmap use case, validator, repository, and controller dependency.
    /// <summary>Verifies all module registrations are connected in the production dependency graph.</summary>
    [Fact]
    public void Services_ShouldResolveRoadmapDependencies()
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        provider.GetRequiredService<IValidator<CreateRoadmapRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<GetMyRoadmapsRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<UpdateRoadmapRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IRoadmapRepository>().Should().NotBeNull();
        typeof(RoadmapsController).GetConstructors().Single().GetParameters()
            .Select(parameter => provider.GetService(parameter.ParameterType))
            .Should().NotContainNulls();
    }

    // TEST-08: Publish RM-01, RM-02, and RM-04 through both runtime routing and OpenAPI.
    /// <summary>Verifies the endpoints consumed by Scalar are discoverable with their documented verbs.</summary>
    [Fact]
    public async Task RuntimeAndOpenApi_ShouldPublishBothRoadmapOperations()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?
                .ControllerTypeInfo.AsType() == typeof(RoadmapsController))
            .ToArray();
        endpoints.Should().HaveCount(3);

        using var response = await _factory.CreateClient().GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        paths.GetProperty("/api/v1/roadmaps").TryGetProperty("post", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps").TryGetProperty("get", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps/{roadmapId}").TryGetProperty("patch", out _).Should().BeTrue();
    }

    /// <summary>Builds an HTTP request with an application/json body.</summary>
    private static HttpRequestMessage JsonRequest(string method, string path, string json) => new(new HttpMethod(method), path)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>Asserts the standard API error envelope and requested error code.</summary>
    private static async Task AssertErrorCode(HttpResponseMessage response, string expected)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("errorCode").GetString().Should().Be(expected);
    }

    /// <summary>Creates a signed access token with the role used by the HTTP scenario.</summary>
    private static string Token(string role)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(RoadmapApiFactory.Secret)),
            SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            RoadmapApiFactory.Issuer,
            RoadmapApiFactory.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "12"), new Claim("role", role)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials));
    }
}

public sealed class RoadmapApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "ANGI.RoadmapHttpTests";
    public const string Audience = "ANGI.RoadmapHttpTests.Client";
    public const string Secret = "ANGI-104-http-contract-test-secret-key-123456789";

    /// <summary>Gets the stateful RM-04 stub used to inspect bound PATCH requests.</summary>
    public StubUpdateRoadmapUseCase Update { get; } = new();

    /// <summary>Gets the stateful RM-02 stub used to inspect bound query parameters.</summary>
    public StubGetMyRoadmapsUseCase GetMyRoadmaps { get; } = new();

    /// <summary>Provides JWT values before Program performs its immediate authentication configuration.</summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var previousIssuer = Environment.GetEnvironmentVariable("Jwt__Issuer");
        var previousAudience = Environment.GetEnvironmentVariable("Jwt__Audience");
        var previousSecret = Environment.GetEnvironmentVariable("Jwt__SecretKey");
        Environment.SetEnvironmentVariable("Jwt__Issuer", Issuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", Audience);
        Environment.SetEnvironmentVariable("Jwt__SecretKey", Secret);
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Jwt__Issuer", previousIssuer);
            Environment.SetEnvironmentVariable("Jwt__Audience", previousAudience);
            Environment.SetEnvironmentVariable("Jwt__SecretKey", previousSecret);
        }
    }

    /// <summary>Configures deterministic authentication and replaces database-dependent use cases.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Error);
        });
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:SecretKey"] = Secret,
                ["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEnsureActiveAccountUseCase>();
            services.RemoveAll<ICreateRoadmapUseCase>();
            services.RemoveAll<IGetMyRoadmapsUseCase>();
            services.RemoveAll<IUpdateRoadmapUseCase>();
            services.AddScoped<IEnsureActiveAccountUseCase, NoOpAccountCheck>();
            services.AddScoped<ICreateRoadmapUseCase, StubCreateRoadmapUseCase>();
            services.AddSingleton(GetMyRoadmaps);
            services.AddSingleton<IGetMyRoadmapsUseCase>(GetMyRoadmaps);
            services.AddSingleton(Update);
            services.AddSingleton<IUpdateRoadmapUseCase>(Update);
        });
    }

    private sealed class NoOpAccountCheck : IEnsureActiveAccountUseCase
    {
        /// <summary>Allows contract tests to bypass database-backed account checks.</summary>
        public Task ExecuteAsync(int userId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubCreateRoadmapUseCase : ICreateRoadmapUseCase
    {
        /// <summary>Returns deterministic empty days for RM-01 serialization tests.</summary>
        public Task<RoadmapDetailDto> ExecuteAsync(CreateRoadmapRequestDto request, CancellationToken ct) =>
            Task.FromResult(Detail(request.NumDays));
    }

    public sealed class StubGetMyRoadmapsUseCase : IGetMyRoadmapsUseCase
    {
        public GetMyRoadmapsRequestDto? LastRequest { get; private set; }

        /// <summary>Captures the bound query and returns a deterministic RM-02 page.</summary>
        public Task<Paged<RoadmapSummaryDto>> ExecuteAsync(
            GetMyRoadmapsRequestDto request,
            CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new Paged<RoadmapSummaryDto>
            {
                Items =
                [
                    new RoadmapSummaryDto
                    {
                        Id = 88,
                        Title = "Trip",
                        NumDays = 3,
                        OriginMode = "pinned",
                        OriginLatitude = 16.061204m,
                        OriginLongitude = 108.227531m
                    }
                ],
                Page = request.Page,
                PageSize = request.PageSize,
                TotalItems = 11,
                TotalPages = 2
            });
        }
    }

    public sealed class StubUpdateRoadmapUseCase : IUpdateRoadmapUseCase
    {
        public UpdateRoadmapRequestDto? LastRequest { get; private set; }

        /// <summary>Captures the bound PATCH request and returns a deterministic detail.</summary>
        public Task<RoadmapDetailDto> ExecuteAsync(long roadmapId, UpdateRoadmapRequestDto request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Detail(1));
        }
    }

    /// <summary>Builds a deterministic empty-roadmap response for HTTP serialization.</summary>
    private static RoadmapDetailDto Detail(short numDays) => new()
    {
        Overview = new RoadmapSummaryDto { Id = 88, Title = "Trip", NumDays = numDays, OriginMode = "none" },
        Itinerary = Enumerable.Range(1, numDays).Select(day => new RoadmapDayDto { DayNumber = (short)day }).ToArray(),
        Budget = new RoadmapBudgetDto()
    };
}
