using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Validators.Administration;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.IntegrationTests.WebApi.Controllers.Administration;

/// <summary>Exercises ANGI-175 routing, binding, authorization, serialization, and error envelopes.</summary>
public sealed class ModsHttpContractTests : IClassFixture<ModsApiFactory>
{
    private readonly ModsApiFactory _factory;

    /// <summary>Initializes the HTTP contract suite with its isolated host factory.</summary>
    public ModsHttpContractTests(ModsApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Bind every ADM-M1 query field and serialize the paged response.
    /// <summary>Verifies ADM-M1 route, query binding, API envelope, and camelCase card fields.</summary>
    [Fact]
    public async Task List_WithAdminToken_ShouldBindAndReturnPage()
    {
        using var client = AdminClient();

        using var response = await client.GetAsync("/api/v1/admin/mods?q=Linh&status=deactivated&page=2&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.List.LastRequest.Should().BeEquivalentTo(new
        {
            Q = "Linh",
            Status = "deactivated",
            Page = 2,
            PageSize = 10
        });
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"totalItems\":1");
        json.Should().Contain("\"displayName\":\"Mod Linh\"");
    }

    // TEST-02: Bind ADM-M2 nested overrides and return HTTP 201.
    /// <summary>Verifies creation body binding, nested arrays, and the documented success status.</summary>
    [Fact]
    public async Task Create_WithValidBody_ShouldReturnCreated()
    {
        using var client = AdminClient();
        var request = new CreateModRequestDto
        {
            Email = "linh@angi.vn",
            DisplayName = "Mod Linh",
            PermissionOverrides = [new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = "deny" }]
        };

        using var response = await client.PostAsJsonAsync("/api/v1/admin/mods", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.Create.LastRequest.Should().NotBeNull();
        _factory.Create.LastRequest!.PermissionOverrides.Should().ContainSingle()
            .Which.PermissionCode.Should().Be("USER_BAN");
    }

    // TEST-03: Return ADM-M3 nested permissions and handled counts.
    /// <summary>Verifies route binding and all documented detail response regions.</summary>
    [Fact]
    public async Task Get_WithAdminToken_ShouldReturnNestedDetail()
    {
        using var client = AdminClient();

        using var response = await client.GetAsync("/api/v1/admin/mods/42");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Get.LastUserId.Should().Be(42);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"permissions\"");
        json.Should().Contain("\"effective\"");
        json.Should().Contain("\"handled\"");
        json.Should().Contain("\"verifications\":37");
    }

    // TEST-04: Bind the ADM-M4 partial body and return the updated card.
    /// <summary>Verifies PATCH route/body binding and response serialization.</summary>
    [Fact]
    public async Task Update_WithValidBody_ShouldReturnUpdatedCard()
    {
        using var client = AdminClient();

        using var response = await client.PatchAsJsonAsync(
            "/api/v1/admin/mods/42",
            new UpdateModRequestDto { Status = "deactivated" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Update.LastCall!.Value.UserId.Should().Be(42);
        _factory.Update.LastCall.Value.Request.Status.Should().Be("deactivated");
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"status\":\"deactivated\"");
    }

    // TEST-05: Envelope ADM-M3 missing-account failures as MOD_NOT_FOUND.
    /// <summary>Verifies the exception middleware preserves the documented HTTP 404 error.</summary>
    [Fact]
    public async Task Get_WithoutMod_ShouldReturnNotFoundEnvelope()
    {
        using var client = AdminClient();

        using var response = await client.GetAsync("/api/v1/admin/mods/404");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        envelope!.Success.Should().BeFalse();
        envelope.ErrorCode.Should().Be("MOD_NOT_FOUND");
    }

    // TEST-06: Envelope invalid ADM-M2 and ADM-M4 bodies as VALIDATION_FAILED.
    /// <summary>Verifies FluentValidation failures retain field-keyed HTTP 400 envelopes.</summary>
    [Theory]
    [InlineData("POST", "/api/v1/admin/mods", "{}")]
    [InlineData("PATCH", "/api/v1/admin/mods/42", "{}")]
    public async Task Write_WithInvalidBody_ShouldReturnValidationEnvelope(
        string method,
        string path,
        string body)
    {
        using var client = AdminClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        envelope!.ErrorCode.Should().Be("VALIDATION_FAILED");
        envelope.Errors.Should().NotBeEmpty();
    }

    // TEST-07: Require a valid ADMIN token for every MOD administration route.
    /// <summary>Verifies missing tokens receive the shared UNAUTHORIZED envelope.</summary>
    [Fact]
    public async Task List_WithoutToken_ShouldReturnUnauthorizedEnvelope()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/admin/mods");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-08: Reject authenticated non-ADMIN callers.
    /// <summary>Verifies role authorization emits the shared FORBIDDEN envelope.</summary>
    [Fact]
    public async Task List_WithModToken_ShouldReturnForbiddenEnvelope()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("MOD"));

        using var response = await client.GetAsync("/api/v1/admin/mods");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("FORBIDDEN");
    }

    /// <summary>Creates an HTTP client carrying a valid ADMIN access token.</summary>
    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("ADMIN"));
        return client;
    }

    /// <summary>Creates a signed access token with the supplied role.</summary>
    private static string Token(string role)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ModsApiFactory.Secret)),
            SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            ModsApiFactory.Issuer,
            ModsApiFactory.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "7"), new Claim("role", role)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials));
    }
}

/// <summary>Hosts the real web pipeline with deterministic MOD use-case boundaries.</summary>
public sealed class ModsApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "ANGI.ModsHttpTests";
    public const string Audience = "ANGI.ModsHttpTests.Client";
    public const string Secret = "ANGI-175-http-contract-test-secret-key-123456789";

    /// <summary>Gets the stateful ADM-M1 stub used to inspect query binding.</summary>
    public StubListModsUseCase List { get; } = new();

    /// <summary>Gets the stateful ADM-M2 stub used to inspect body binding.</summary>
    public StubCreateModUseCase Create { get; } = new();

    /// <summary>Gets the stateful ADM-M3 stub used to inspect route binding.</summary>
    public StubGetModDetailUseCase Get { get; } = new();

    /// <summary>Gets the stateful ADM-M4 stub used to inspect route and body binding.</summary>
    public StubUpdateModUseCase Update { get; } = new();

    /// <summary>Provides JWT settings before Program performs immediate authentication configuration.</summary>
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

    /// <summary>Replaces database-dependent use cases with deterministic HTTP-boundary stubs.</summary>
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
            services.RemoveAll<IListModsUseCase>();
            services.RemoveAll<ICreateModUseCase>();
            services.RemoveAll<IGetModDetailUseCase>();
            services.RemoveAll<IUpdateModUseCase>();
            services.AddScoped<IEnsureActiveAccountUseCase, NoOpAccountCheck>();
            services.AddSingleton(List);
            services.AddSingleton<IListModsUseCase>(List);
            services.AddSingleton(Create);
            services.AddSingleton<ICreateModUseCase>(Create);
            services.AddSingleton(Get);
            services.AddSingleton<IGetModDetailUseCase>(Get);
            services.AddSingleton(Update);
            services.AddSingleton<IUpdateModUseCase>(Update);
        });
    }

    private sealed class NoOpAccountCheck : IEnsureActiveAccountUseCase
    {
        /// <summary>Allows HTTP contract tests to bypass database-backed status checks.</summary>
        public Task ExecuteAsync(int userId, CancellationToken ct) => Task.CompletedTask;
    }

    public sealed class StubListModsUseCase : IListModsUseCase
    {
        public ListModsRequestDto? LastRequest { get; private set; }

        /// <summary>Validates and captures ADM-M1 query values and returns a representative page.</summary>
        public async Task<Paged<AdminUserCardDto>> ExecuteAsync(ListModsRequestDto request, CancellationToken ct)
        {
            await new ListModsRequestDtoValidator().ValidateAndThrowAsync(request, ct);
            LastRequest = request;
            return new Paged<AdminUserCardDto>
            {
                Items = [Card(request.Status ?? "active")],
                Page = request.Page,
                PageSize = request.PageSize,
                TotalItems = 1,
                TotalPages = 1
            };
        }
    }

    public sealed class StubCreateModUseCase : ICreateModUseCase
    {
        public CreateModRequestDto? LastRequest { get; private set; }

        /// <summary>Validates and captures ADM-M2 body values and returns the created card.</summary>
        public async Task<AdminUserCardDto> ExecuteAsync(CreateModRequestDto request, CancellationToken ct)
        {
            await new CreateModRequestDtoValidator().ValidateAndThrowAsync(request, ct);
            LastRequest = request;
            if (request.PermissionOverrides?.Any(item => item.PermissionCode == "UNKNOWN") == true)
            {
                throw new BadRequestException("UNKNOWN_PERMISSION", "Mã quyền không tồn tại.");
            }

            return Card("active");
        }
    }

    public sealed class StubGetModDetailUseCase : IGetModDetailUseCase
    {
        public int? LastUserId { get; private set; }

        /// <summary>Captures the route ID and returns details or MOD_NOT_FOUND.</summary>
        public Task<ModDetailDto> ExecuteAsync(int userId, CancellationToken ct)
        {
            LastUserId = userId;
            if (userId == 404)
            {
                throw new NotFoundException("MOD_NOT_FOUND", "Không tìm thấy tài khoản Mod.");
            }

            var card = Card("active");
            return Task.FromResult(new ModDetailDto
            {
                Id = card.Id,
                Email = card.Email,
                DisplayName = card.DisplayName,
                Role = card.Role,
                Status = card.Status,
                CreatedAt = card.CreatedAt,
                Permissions = new ModPermissionsDto { Effective = ["USER_BAN"] },
                Handled = new ModHandledDto { Verifications = 37, Menus = 52, Reports = 80 }
            });
        }
    }

    public sealed class StubUpdateModUseCase : IUpdateModUseCase
    {
        public (int UserId, UpdateModRequestDto Request)? LastCall { get; private set; }

        /// <summary>Validates and captures ADM-M4 route and body values and returns the updated card.</summary>
        public async Task<AdminUserCardDto> ExecuteAsync(
            int userId,
            UpdateModRequestDto request,
            CancellationToken ct)
        {
            await new UpdateModRequestDtoValidator().ValidateAndThrowAsync(request, ct);
            LastCall = (userId, request);
            return Card(request.Status ?? "active");
        }
    }

    /// <summary>Creates a representative MOD card for contract serialization.</summary>
    private static AdminUserCardDto Card(string status) => new()
    {
        Id = 42,
        Email = "linh@angi.vn",
        DisplayName = "Mod Linh",
        Role = "MOD",
        Status = status,
        CreatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    };
}
