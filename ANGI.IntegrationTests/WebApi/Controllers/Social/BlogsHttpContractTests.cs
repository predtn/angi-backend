using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Social;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.IntegrationTests.WebApi.Controllers.Social;

/// <summary>HTTP routing, authorization, binding, error, serialization, and OpenAPI tests for BLOG-04.</summary>
public sealed class BlogsHttpContractTests : IClassFixture<BlogsApiFactory>
{
    private readonly BlogsApiFactory _factory;

    /// <summary>Initializes BLOG-04 HTTP tests with a deterministic in-memory boundary.</summary>
    public BlogsHttpContractTests(BlogsApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Return the canonical 401 envelope when BLOG-04 has no token.
    /// <summary>Verifies the runtime authorization pipeline protects blog creation.</summary>
    [Fact]
    public async Task Create_WithoutToken_ShouldReturnUnauthorizedEnvelope()
    {
        using var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/blogs", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertError(response, "UNAUTHORIZED");
    }

    // TEST-02: Return the canonical 403 envelope when BLOG-04 receives a non-Traveler token.
    /// <summary>Verifies role authorization rejects restaurant owners before use-case execution.</summary>
    [Fact]
    public async Task Create_WithWrongRole_ShouldReturnForbiddenEnvelope()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/blogs")
        {
            Content = JsonContent.Create(ValidBody())
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("RESTAURANT_OWNER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertError(response, "FORBIDDEN");
    }

    // TEST-03: Bind every BLOG-04 field and serialize the complete BlogDetail envelope with HTTP 201.
    /// <summary>Verifies route, body binding, delegation, nullable fields, and response JSON shape.</summary>
    [Fact]
    public async Task Create_WithValidTravelerRequest_ShouldReturnCompleteCreatedEnvelope()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/blogs")
        {
            Content = JsonContent.Create(ValidBody())
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("message").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("errorCode").ValueKind.Should().Be(JsonValueKind.Null);
        var data = root.GetProperty("data");
        data.GetProperty("id").GetInt64().Should().Be(45);
        data.GetProperty("author").GetProperty("displayName").GetString().Should().Be("Nguyễn An");
        data.GetProperty("images").GetArrayLength().Should().Be(2);
        data.GetProperty("roadmapSnapshot").GetProperty("days")[0]
            .GetProperty("items")[0].GetProperty("mealSlot").GetString().Should().Be("breakfast");
        data.GetProperty("likedByMe").GetBoolean().Should().BeFalse();
        data.GetProperty("isMine").GetBoolean().Should().BeTrue();
        _factory.Stub.LastRequest!.CoverMediaId.Should().Be(9400);
        _factory.Stub.LastRequest.ImageMediaIds.Should().Equal(9401, 9402);
        _factory.Stub.LastRequest.RoadmapId.Should().Be(88);
    }

    // TEST-04: Convert missing, null, malformed, and non-JSON BLOG-04 bodies to VALIDATION_FAILED.
    /// <summary>Verifies all model-binding failure regions use the canonical API envelope.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("null", "application/json")]
    [InlineData("{", "application/json")]
    [InlineData("title=x", "application/x-www-form-urlencoded")]
    public async Task Create_WithInvalidBody_ShouldReturnValidationEnvelope(string? body, string? contentType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/blogs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType!);
        }

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        await AssertError(response, "VALIDATION_FAILED");
    }

    // TEST-05: Preserve ROADMAP_NOT_FOUND through the BLOG-04 exception middleware boundary.
    /// <summary>Verifies the documented missing-roadmap status and error code over real HTTP.</summary>
    [Fact]
    public async Task Create_WhenRoadmapIsUnavailable_ShouldReturnNotFoundEnvelope()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/blogs")
        {
            Content = JsonContent.Create(new { title = "Bài", content = "Nội dung", roadmapId = 404L })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertError(response, "ROADMAP_NOT_FOUND");
    }

    // TEST-06: Publish BLOG-04 and its request and response schemas in the generated OpenAPI document.
    /// <summary>Verifies Scalar consumers can discover the implemented endpoint contract.</summary>
    [Fact]
    public async Task OpenApiDocument_ShouldContainBlogCreateOperationAndSchemas()
    {
        using var response = await _factory.CreateClient().GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/blogs").GetProperty("post");
        operation.GetProperty("requestBody").GetProperty("content")
            .TryGetProperty("application/json", out _).Should().BeTrue();
        operation.GetProperty("responses").TryGetProperty("201", out _).Should().BeTrue();
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.EnumerateObject().Any(schema => schema.Value.TryGetProperty("properties", out var properties) &&
            properties.TryGetProperty("imageMediaIds", out _)).Should().BeTrue();
        schemas.EnumerateObject().Any(schema => schema.Value.TryGetProperty("properties", out var properties) &&
            properties.TryGetProperty("roadmapSnapshot", out _)).Should().BeTrue();
    }

    /// <summary>Creates a request containing every BLOG-04 field.</summary>
    private static CreateBlogRequestDto ValidBody() => new()
    {
        Title = "Hành trình Đà Nẵng",
        Content = "<p>Ngày đầu tiên...</p>",
        CoverMediaId = 9400,
        ImageMediaIds = [9401, 9402],
        RoadmapId = 88
    };

    /// <summary>Creates a signed JWT accepted by the deterministic HTTP host.</summary>
    private static string Token(string role)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(BlogsApiFactory.Secret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            BlogsApiFactory.Issuer,
            BlogsApiFactory.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "12"), new Claim("role", role)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Asserts the standard error fields returned by the WebApi pipeline.</summary>
    private static async Task AssertError(HttpResponseMessage response, string errorCode)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        document.RootElement.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        document.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
    }
}

/// <summary>Hosts BLOG-04 HTTP tests with deterministic authentication and use-case results.</summary>
public sealed class BlogsApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "ANGI.Blogs.HttpTests";
    public const string Audience = "ANGI.Blogs.HttpTests.Client";
    public const string Secret = "ANGI-141-http-contract-test-secret-key-123456789";

    /// <summary>Gets the singleton stub used to inspect the request bound by MVC.</summary>
    public StubCreateBlogUseCase Stub { get; } = new();

    /// <summary>Configures deterministic authentication and replaces database-dependent request services.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());
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
            services.RemoveAll<IConfigureOptions<JwtBearerOptions>>();
            services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure(options => ConfigureJwt(options));
            services.RemoveAll<IEnsureActiveAccountUseCase>();
            services.AddScoped<IEnsureActiveAccountUseCase, NoOpAccountCheck>();
            services.RemoveAll<ICreateBlogUseCase>();
            services.AddSingleton(Stub);
            services.AddSingleton<ICreateBlogUseCase>(Stub);
        });
    }

    /// <summary>Configures JWT validation and standard challenge and forbidden envelopes for the test host.</summary>
    private static void ConfigureJwt(JwtBearerOptions options)
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
    }

    private sealed class NoOpAccountCheck : IEnsureActiveAccountUseCase
    {
        /// <summary>Allows authenticated HTTP contract requests without a database account lookup.</summary>
        public Task ExecuteAsync(int userId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Returns deterministic BLOG-04 data and records the model bound by MVC.</summary>
    public sealed class StubCreateBlogUseCase : ICreateBlogUseCase
    {
        /// <summary>Gets the most recent BLOG-04 request delegated by the controller.</summary>
        public CreateBlogRequestDto? LastRequest { get; private set; }

        /// <summary>Returns a full BlogDetail or the documented missing-roadmap error.</summary>
        public Task<BlogDetailDto> ExecuteAsync(CreateBlogRequestDto request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.RoadmapId == 404)
            {
                throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
            }

            return Task.FromResult(new BlogDetailDto
            {
                Id = 45,
                Title = request.Title,
                Slug = "hanh-trinh-da-nang",
                Excerpt = "Ngày đầu tiên...",
                CoverUrl = "https://cdn.test/cover.jpg",
                Author = new UserSummaryDto { Id = 12, DisplayName = "Nguyễn An", AvatarUrl = null },
                HasRoadmap = true,
                PublishedAt = new DateTime(2026, 10, 19, 3, 30, 0, DateTimeKind.Utc),
                Content = request.Content,
                Images = ["https://cdn.test/1.jpg", "https://cdn.test/2.jpg"],
                RoadmapId = request.RoadmapId,
                RoadmapSnapshot = new RoadmapSnapshotDto
                {
                    RoadmapId = 88,
                    Title = "Chuyến đi",
                    NumDays = 1,
                    CapturedAt = new DateTime(2026, 10, 19, 3, 30, 0, DateTimeKind.Utc),
                    Days = [new RoadmapSnapshotDayDto
                    {
                        DayNumber = 1,
                        Items = [new RoadmapSnapshotItemDto { MealSlot = "breakfast", DishName = "Bún bò" }]
                    }]
                },
                IsMine = true,
                UpdatedAt = new DateTime(2026, 10, 19, 3, 30, 0, DateTimeKind.Utc)
            });
        }
    }
}
