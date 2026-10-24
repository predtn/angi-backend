using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Validators.Administration;
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

public sealed class PermissionsHttpContractTests : IClassFixture<PermissionsApiFactory>
{
    private readonly PermissionsApiFactory _factory;

    /// <summary>Initializes permission HTTP contract tests with the shared deterministic API host.</summary>
    public PermissionsHttpContractTests(PermissionsApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Return the canonical 401 envelope for every ANGI-170 endpoint without a token.
    /// <summary>Verifies ADM-P1 through ADM-P4 all require authentication.</summary>
    [Theory]
    [InlineData("GET", "/api/v1/admin/permissions", null)]
    [InlineData("GET", "/api/v1/admin/mods/42/permissions", null)]
    [InlineData("PUT", "/api/v1/admin/mods/42/permissions", "{\"overrides\":[]}")]
    [InlineData("PUT", "/api/v1/admin/roles/MOD/permissions", "{\"permissionCodes\":[]}")]
    public async Task Endpoint_WithoutToken_ShouldReturnUnauthorized(string method, string path, string? body)
    {
        using var request = Request(method, path, body);

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertErrorCode(response, "UNAUTHORIZED");
    }

    // TEST-02: Return the canonical 403 envelope for every ANGI-170 endpoint with a non-admin role.
    /// <summary>Verifies the controller enforces the documented ADMIN role on all four routes.</summary>
    [Theory]
    [InlineData("GET", "/api/v1/admin/permissions", null)]
    [InlineData("GET", "/api/v1/admin/mods/42/permissions", null)]
    [InlineData("PUT", "/api/v1/admin/mods/42/permissions", "{\"overrides\":[]}")]
    [InlineData("PUT", "/api/v1/admin/roles/MOD/permissions", "{\"permissionCodes\":[]}")]
    public async Task Endpoint_WithNonAdminRole_ShouldReturnForbidden(string method, string path, string? body)
    {
        using var request = Request(method, path, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("MOD"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertErrorCode(response, "FORBIDDEN");
    }

    // TEST-03: Serialize ADM-P1 with uppercase role keys and the ADMIN wildcard.
    /// <summary>Verifies routing, delegation, envelope shape, and the case-sensitive role-default contract.</summary>
    [Fact]
    public async Task GetPermissionCatalog_AsAdmin_ShouldSerializeDocumentedShape()
    {
        using var request = Request("GET", "/api/v1/admin/permissions", null);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("ADMIN"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("permissions")[0].GetProperty("code").GetString().Should().Be("USER_BAN");
        data.GetProperty("roleDefaults").GetProperty("MOD")[0].GetString().Should().Be("USER_BAN");
        data.GetProperty("roleDefaults").GetProperty("ADMIN")[0].GetString().Should().Be("*");
    }

    // TEST-04: Serialize ADM-P2 role defaults, overrides, and effective permissions.
    /// <summary>Verifies the userId route binding and complete ModPermissions JSON shape.</summary>
    [Fact]
    public async Task GetModPermissions_AsAdmin_ShouldSerializeDocumentedShape()
    {
        using var request = Request("GET", "/api/v1/admin/mods/42/permissions", null);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("ADMIN"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("roleDefaults")[0].GetString().Should().Be("USER_BAN");
        data.GetProperty("overrides")[0].GetProperty("effect").GetString().Should().Be("deny");
        data.GetProperty("effective")[0].GetString().Should().Be("DASHBOARD_VIEW");
        _factory.GetMod.LastUserId.Should().Be(42);
    }

    // TEST-05: Bind ADM-P3 nested overrides and serialize the replacement response.
    /// <summary>Verifies nested request binding, use-case validation, and the complete success envelope.</summary>
    [Fact]
    public async Task ReplaceModPermissions_WithValidBody_ShouldBindAndSerializeResponse()
    {
        using var request = Request("PUT", "/api/v1/admin/mods/42/permissions", """
            {"overrides":[{"permissionCode":"USER_BAN","effect":"deny"}]}
            """);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("ADMIN"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.ReplaceMod.LastCall.Should().NotBeNull();
        _factory.ReplaceMod.LastCall!.Value.UserId.Should().Be(42);
        _factory.ReplaceMod.LastCall.Value.Request.Overrides[0].PermissionCode.Should().Be("USER_BAN");
    }

    // TEST-06: Bind ADM-P4 codes and serialize role and resulting defaults.
    /// <summary>Verifies replacement-array binding and the complete ADM-P4 response shape.</summary>
    [Fact]
    public async Task ReplaceModRolePermissions_WithValidBody_ShouldBindAndSerializeResponse()
    {
        using var request = Request("PUT", "/api/v1/admin/roles/MOD/permissions", """
            {"permissionCodes":["USER_BAN","DASHBOARD_VIEW"]}
            """);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("ADMIN"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("role").GetString().Should().Be("MOD");
        data.GetProperty("permissionCodes").GetArrayLength().Should().Be(2);
    }

    // TEST-07: Return VALIDATION_FAILED for missing and explicit-null PUT bodies.
    /// <summary>Verifies model-binding and FluentValidation failures retain the canonical HTTP envelope.</summary>
    [Theory]
    [InlineData("/api/v1/admin/mods/42/permissions", null)]
    [InlineData("/api/v1/admin/mods/42/permissions", "{\"overrides\":null}")]
    [InlineData("/api/v1/admin/roles/MOD/permissions", null)]
    [InlineData("/api/v1/admin/roles/MOD/permissions", "{\"permissionCodes\":null}")]
    public async Task PutEndpoint_WithMissingRequiredPayload_ShouldReturnValidationEnvelope(string path, string? body)
    {
        using var request = Request("PUT", path, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("ADMIN"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertErrorCode(response, "VALIDATION_FAILED");
    }

    // TEST-08: Preserve each documented business error through the HTTP exception boundary.
    /// <summary>Verifies MOD_NOT_FOUND, UNKNOWN_PERMISSION, and PERMISSION_NOT_GRANTABLE status mappings.</summary>
    [Theory]
    [InlineData("GET", "/api/v1/admin/mods/404/permissions", null, HttpStatusCode.NotFound, "MOD_NOT_FOUND")]
    [InlineData("PUT", "/api/v1/admin/mods/42/permissions", "{\"overrides\":[{\"permissionCode\":\"UNKNOWN\",\"effect\":\"allow\"}]}", HttpStatusCode.BadRequest, "UNKNOWN_PERMISSION")]
    [InlineData("PUT", "/api/v1/admin/roles/MOD/permissions", "{\"permissionCodes\":[\"MOD_MANAGE\"]}", HttpStatusCode.BadRequest, "PERMISSION_NOT_GRANTABLE")]
    public async Task Endpoint_WithBusinessFailure_ShouldReturnDocumentedEnvelope(
        string method,
        string path,
        string? body,
        HttpStatusCode status,
        string errorCode)
    {
        using var request = Request(method, path, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("ADMIN"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(status);
        await AssertErrorCode(response, errorCode);
    }

    /// <summary>Builds an HTTP request and adds JSON content when a body was supplied.</summary>
    private static HttpRequestMessage Request(string method, string path, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>Asserts the standard error envelope and requested error code.</summary>
    private static async Task AssertErrorCode(HttpResponseMessage response, string expected)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("errorCode").GetString().Should().Be(expected);
    }

    /// <summary>Creates a signed access token with the supplied role.</summary>
    private static string Token(string role)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(PermissionsApiFactory.Secret)),
            SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            PermissionsApiFactory.Issuer,
            PermissionsApiFactory.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "7"), new Claim("role", role)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials));
    }
}

public sealed class PermissionsApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "ANGI.PermissionsHttpTests";
    public const string Audience = "ANGI.PermissionsHttpTests.Client";
    public const string Secret = "ANGI-170-http-contract-test-secret-key-123456789";

    /// <summary>Gets the stateful ADM-P2 stub used to inspect route binding.</summary>
    public StubGetModPermissionsUseCase GetMod { get; } = new();

    /// <summary>Gets the stateful ADM-P3 stub used to inspect route and body binding.</summary>
    public StubReplaceModPermissionsUseCase ReplaceMod { get; } = new();

    /// <summary>Provides JWT values before Program performs immediate authentication configuration.</summary>
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
            services.RemoveAll<IGetPermissionCatalogUseCase>();
            services.RemoveAll<IGetModPermissionsUseCase>();
            services.RemoveAll<IReplaceModPermissionsUseCase>();
            services.RemoveAll<IReplaceModRolePermissionsUseCase>();
            services.AddScoped<IEnsureActiveAccountUseCase, NoOpAccountCheck>();
            services.AddScoped<IGetPermissionCatalogUseCase, StubGetPermissionCatalogUseCase>();
            services.AddSingleton(GetMod);
            services.AddSingleton<IGetModPermissionsUseCase>(GetMod);
            services.AddSingleton(ReplaceMod);
            services.AddSingleton<IReplaceModPermissionsUseCase>(ReplaceMod);
            services.AddScoped<IReplaceModRolePermissionsUseCase, StubReplaceModRolePermissionsUseCase>();
        });
    }

    private sealed class NoOpAccountCheck : IEnsureActiveAccountUseCase
    {
        /// <summary>Allows contract tests to bypass database-backed account status checks.</summary>
        public Task ExecuteAsync(int userId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubGetPermissionCatalogUseCase : IGetPermissionCatalogUseCase
    {
        /// <summary>Returns a representative ADM-P1 payload for serialization checks.</summary>
        public Task<PermissionCatalogDto> ExecuteAsync(CancellationToken ct) => Task.FromResult(new PermissionCatalogDto
        {
            Permissions = [new PermissionDto { Code = "USER_BAN", Description = "Ban user" }],
            RoleDefaults = new RolePermissionDefaultsDto { Mod = ["USER_BAN"], Admin = ["*"] }
        });
    }

    public sealed class StubGetModPermissionsUseCase : IGetModPermissionsUseCase
    {
        public int? LastUserId { get; private set; }

        /// <summary>Captures the route ID, returns representative data, or emits MOD_NOT_FOUND.</summary>
        public Task<ModPermissionsDto> ExecuteAsync(int userId, CancellationToken ct)
        {
            LastUserId = userId;
            if (userId == 404)
            {
                throw new NotFoundException("MOD_NOT_FOUND", "Không tìm thấy tài khoản Mod.");
            }

            return Task.FromResult(new ModPermissionsDto
            {
                RoleDefaults = ["USER_BAN"],
                Overrides = [new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = "deny" }],
                Effective = ["DASHBOARD_VIEW"]
            });
        }
    }

    public sealed class StubReplaceModPermissionsUseCase : IReplaceModPermissionsUseCase
    {
        public (int UserId, ReplaceModPermissionsRequestDto Request)? LastCall { get; private set; }

        /// <summary>Validates and captures ADM-P3 input and emits representative business failures.</summary>
        public async Task<ModPermissionsDto> ExecuteAsync(
            int userId,
            ReplaceModPermissionsRequestDto request,
            CancellationToken ct)
        {
            await new ReplaceModPermissionsRequestDtoValidator().ValidateAndThrowAsync(request, ct);
            LastCall = (userId, request);
            if (request.Overrides.Any(item => item.PermissionCode == "UNKNOWN"))
            {
                throw new BadRequestException("UNKNOWN_PERMISSION", "Mã quyền không tồn tại.");
            }

            return new ModPermissionsDto
            {
                RoleDefaults = ["USER_BAN"],
                Overrides = request.Overrides,
                Effective = []
            };
        }
    }

    private sealed class StubReplaceModRolePermissionsUseCase : IReplaceModRolePermissionsUseCase
    {
        /// <summary>Validates ADM-P4 input and emits its restricted-permission business failure.</summary>
        public async Task<ReplaceModRolePermissionsResponseDto> ExecuteAsync(
            ReplaceModRolePermissionsRequestDto request,
            CancellationToken ct)
        {
            await new ReplaceModRolePermissionsRequestDtoValidator().ValidateAndThrowAsync(request, ct);
            if (request.PermissionCodes.Contains("MOD_MANAGE", StringComparer.Ordinal))
            {
                throw new BadRequestException("PERMISSION_NOT_GRANTABLE", "Quyền này chỉ dành cho quản trị viên.");
            }

            return new ReplaceModRolePermissionsResponseDto
            {
                Role = "MOD",
                PermissionCodes = request.PermissionCodes
            };
        }
    }
}
