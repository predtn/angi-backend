using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
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

namespace ANGI.IntegrationTests.WebApi.Controllers.Moderation;

/// <summary>Exercises MOD-V1 through MOD-V3 routing, authorization, binding, and response envelopes over HTTP.</summary>
public sealed class VerificationsHttpContractTests : IClassFixture<VerificationsApiFactory>
{
    private readonly VerificationsApiFactory _factory;

    /// <summary>Initializes the suite with its deterministic HTTP host.</summary>
    public VerificationsHttpContractTests(VerificationsApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Lists every ANGI-178 endpoint and its valid request body when required.</summary>
    public static TheoryData<HttpMethod, string, object?> Endpoints => new()
    {
        { HttpMethod.Get, "/api/v1/mod/verifications", null },
        { HttpMethod.Get, "/api/v1/mod/verifications/21", null },
        { HttpMethod.Post, "/api/v1/mod/verifications/21/decision", new { decision = "approve" } }
    };

    // TEST-01: Return the canonical 401 envelope for all ANGI-178 endpoints without a token.
    /// <summary>Verifies authentication is enforced consistently across MOD-V1 through MOD-V3.</summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_WithoutToken_ShouldReturnUnauthorized(HttpMethod method, string path, object? body)
    {
        using var request = Request(method, path, body);

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertErrorAsync(response, "UNAUTHORIZED");
    }

    // TEST-02: Return the canonical 403 envelope for all ANGI-178 endpoints with the wrong role.
    /// <summary>Verifies role authorization rejects travelers before controller execution.</summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_WithTravelerRole_ShouldReturnForbidden(HttpMethod method, string path, object? body)
    {
        using var client = Client("TRAVELER");
        using var request = Request(method, path, body);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertErrorAsync(response, "FORBIDDEN");
    }

    // TEST-03: Bind MOD-V1 defaults and return the paged success contract for a Mod.
    /// <summary>Verifies the exact list route, default query values, and pagination envelope.</summary>
    [Fact]
    public async Task List_WithModToken_ShouldReturnPagedContract()
    {
        using var client = Client("MOD");

        using var response = await client.GetAsync("/api/v1/mod/verifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("items").GetArrayLength().Should().Be(1);
        data.GetProperty("page").GetInt32().Should().Be(1);
        data.GetProperty("pageSize").GetInt32().Should().Be(20);
        _factory.List.LastRequest!.Status.Should().Be("pending");
    }

    // TEST-04: Return MOD-V2 verification fields flattened beside restaurant and previous.
    /// <summary>Verifies inheritance serializes the documented response shape without an extra verification object.</summary>
    [Fact]
    public async Task Get_WithAdminToken_ShouldReturnFlattenedDetailContract()
    {
        using var client = Client("ADMIN");

        using var response = await client.GetAsync("/api/v1/mod/verifications/21");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("id").GetInt64().Should().Be(21);
        data.GetProperty("restaurant").GetProperty("id").GetInt64().Should().Be(7);
        data.TryGetProperty("previous", out _).Should().BeTrue();
        data.TryGetProperty("verification", out _).Should().BeFalse();
    }

    // TEST-05: Bind MOD-V3 JSON and return the reviewed verification contract.
    /// <summary>Verifies the decision route and body are delegated through the full MVC pipeline.</summary>
    [Fact]
    public async Task Decide_WithValidBody_ShouldReturnVerificationContract()
    {
        using var client = Client("MOD");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/mod/verifications/21/decision",
            new { decision = "reject", reviewNote = "Invalid document" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("rejected");
        _factory.Review.LastVerificationId.Should().Be(21);
        _factory.Review.LastRequest!.ReviewNote.Should().Be("Invalid document");
    }

    // TEST-06: Return VALIDATION_FAILED for a missing MOD-V3 JSON body.
    /// <summary>Verifies null-body model binding uses the canonical API error envelope and never invokes the use case.</summary>
    [Fact]
    public async Task Decide_WithoutBody_ShouldReturnValidationEnvelope()
    {
        _factory.Review.Reset();
        using var client = Client("MOD");

        using var response = await client.PostAsync("/api/v1/mod/verifications/21/decision", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertErrorAsync(response, "VALIDATION_FAILED");
        _factory.Review.LastRequest.Should().BeNull();
    }

    /// <summary>Creates an authenticated client for the requested role.</summary>
    private HttpClient Client(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(role));
        return client;
    }

    /// <summary>Creates a request with an optional JSON body.</summary>
    private static HttpRequestMessage Request(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    /// <summary>Issues a short-lived signed token accepted only by the test host.</summary>
    private static string Token(string role)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(VerificationsApiFactory.Secret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            VerificationsApiFactory.Issuer,
            VerificationsApiFactory.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "9"), new Claim("role", role)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Asserts the canonical error envelope fields.</summary>
    private static async Task AssertErrorAsync(HttpResponseMessage response, string errorCode)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        document.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
    }
}

/// <summary>Hosts ANGI-178 HTTP tests with deterministic JWT authentication and use-case results.</summary>
public sealed class VerificationsApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "ANGI.Verifications.HttpTests";
    public const string Audience = "ANGI.Verifications.HttpTests.Client";
    public const string Secret = "ANGI-178-http-contract-test-secret-key-123456789";

    public StubListUseCase List { get; } = new();
    public StubReviewUseCase Review { get; } = new();

    /// <summary>Configures deterministic auth and replaces all database-dependent ANGI-178 use cases.</summary>
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
                .Configure(ConfigureJwt);
            services.RemoveAll<IEnsureActiveAccountUseCase>();
            services.AddScoped<IEnsureActiveAccountUseCase, NoOpAccountCheck>();
            services.RemoveAll<IListVerificationsUseCase>();
            services.RemoveAll<IGetVerificationReviewUseCase>();
            services.RemoveAll<IReviewVerificationUseCase>();
            services.AddSingleton(List);
            services.AddSingleton<IListVerificationsUseCase>(List);
            services.AddSingleton<IGetVerificationReviewUseCase, StubGetUseCase>();
            services.AddSingleton(Review);
            services.AddSingleton<IReviewVerificationUseCase>(Review);
        });
    }

    /// <summary>Configures JWT validation plus the standard challenge and forbidden envelopes.</summary>
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
        /// <summary>Allows authenticated contract requests without a database account lookup.</summary>
        public Task ExecuteAsync(int userId, CancellationToken ct) => Task.CompletedTask;
    }

    public sealed class StubListUseCase : IListVerificationsUseCase
    {
        public ListVerificationsRequestDto? LastRequest { get; private set; }

        /// <summary>Records the bound MOD-V1 request and returns one deterministic page.</summary>
        public Task<Paged<ModVerificationCardDto>> ExecuteAsync(
            ListVerificationsRequestDto request,
            CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new Paged<ModVerificationCardDto>
            {
                Items = [new ModVerificationCardDto { Id = 21, RestaurantId = 7, Status = "pending" }],
                Page = request.Page,
                PageSize = request.PageSize,
                TotalItems = 1,
                TotalPages = 1
            });
        }
    }

    private sealed class StubGetUseCase : IGetVerificationReviewUseCase
    {
        /// <summary>Returns a deterministic flattened MOD-V2 response.</summary>
        public Task<VerificationReviewDetailDto> ExecuteAsync(long verificationId, CancellationToken ct) =>
            Task.FromResult(new VerificationReviewDetailDto
            {
                Id = verificationId,
                RestaurantId = 7,
                Status = "pending",
                Restaurant = new OwnerRestaurantDto { Id = 7 },
                Previous = null
            });
    }

    public sealed class StubReviewUseCase : IReviewVerificationUseCase
    {
        public long? LastVerificationId { get; private set; }
        public ReviewVerificationRequestDto? LastRequest { get; private set; }

        /// <summary>Records the MOD-V3 request and returns the selected terminal status.</summary>
        public Task<VerificationDto> ExecuteAsync(
            long verificationId,
            ReviewVerificationRequestDto request,
            CancellationToken ct)
        {
            LastVerificationId = verificationId;
            LastRequest = request;
            return Task.FromResult(new VerificationDto
            {
                Id = verificationId,
                RestaurantId = 7,
                Status = request.Decision == "approve" ? "approved" : "rejected"
            });
        }

        /// <summary>Clears captured invocation state between binding assertions.</summary>
        public void Reset()
        {
            LastVerificationId = null;
            LastRequest = null;
        }
    }
}
