using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using ANGI.Application.DTOs.Administration;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Exercises ANGI-170 permission administration through HTTP, real use cases, and PostgreSQL.</summary>
public sealed class AdministrationPermissionsEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public AdministrationPermissionsEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Read the seeded catalog and calculate persisted MOD overrides through real queries.
    /// <summary>Covers ADM-P1 and ADM-P2 across authorization, use cases, repositories, and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task ReadPermissions_ShouldReturnCatalogDefaultsOverridesAndEffectiveSet()
    {
        // STEP-01: Reset the test database, create users through SeedAdminAndModAsync and get client from AuthorizedAdminClient.
        await _factory.ResetDatabaseAsync();
        var users = await SeedAdminAndModAsync(addExistingOverride: true);
        using var client = AuthorizedAdminClient(users.AdminId);

        // STEP-02: Send GET /api/v1/admin/permissions and keep catalogResponse.
        using var catalogResponse = await client.GetAsync("/api/v1/admin/permissions");
        // STEP-03: Send GET /api/v1/admin/mods/{users.ModId}/permissions and keep modResponse.
        using var modResponse = await client.GetAsync($"/api/v1/admin/mods/{users.ModId}/permissions");

        // STEP-04: Assert catalogResponse.StatusCode is HttpStatusCode.OK and modResponse.StatusCode is HttpStatusCode.OK.
        catalogResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        modResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        // STEP-05: Obtain catalog through SuccessDataAsync and mod through SuccessDataAsync.
        var catalog = await SuccessDataAsync<PermissionCatalogDto>(catalogResponse);
        var mod = await SuccessDataAsync<ModPermissionsDto>(modResponse);
        // STEP-06: Assert catalog.Permissions has 13 item(s), catalog.RoleDefaults.Mod has 10 item(s) and catalog.RoleDefaults.Admin equals "*".
        catalog.Permissions.Should().HaveCount(13);
        catalog.RoleDefaults.Mod.Should().HaveCount(10);
        catalog.RoleDefaults.Admin.Should().Equal("*");
        // STEP-07: Assert mod.Overrides has exactly one matching item, mod.Effective does not contain "USER_BAN" and mod.Effective contains "USER_SUSPEND".
        mod.Overrides.Should().ContainSingle(item =>
            item.PermissionCode == "USER_BAN" && item.Effect == "deny");
        mod.Effective.Should().NotContain("USER_BAN");
        mod.Effective.Should().Contain("USER_SUSPEND");
    }

    // TEST-02: Replace MOD overrides, update an existing composite key, and persist its audit atomically.
    /// <summary>Covers ADM-P3 including replace-all semantics, granted_by, effective permissions, and audit data.</summary>
    [PostgresEndToEndFact]
    public async Task ReplaceModPermissions_ShouldPersistCompleteReplacementAndAudit()
    {
        // STEP-01: Reset the test database, create users through SeedAdminAndModAsync and get client from AuthorizedAdminClient.
        await _factory.ResetDatabaseAsync();
        var users = await SeedAdminAndModAsync(addExistingOverride: true);
        using var client = AuthorizedAdminClient(users.AdminId);

        // STEP-02: Send PUT /api/v1/admin/mods/{users.ModId}/permissions with a ReplaceModPermissionsRequestDto body and keep response.
        using var response = await client.PutAsJsonAsync(
            $"/api/v1/admin/mods/{users.ModId}/permissions",
            new ReplaceModPermissionsRequestDto
            {
                Overrides =
                [
                    new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = "deny" },
                    new PermissionOverrideDto { PermissionCode = "DASHBOARD_VIEW", Effect = "allow" }
                ]
            });

        // STEP-03: Obtain data through SuccessDataAsync; assert response.StatusCode is HttpStatusCode.OK, data.Effective does not contain "USER_BAN" and 1 more.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var data = await SuccessDataAsync<ModPermissionsDto>(response);
        data.Effective.Should().NotContain("USER_BAN");
        data.Effective.Should().Contain("DASHBOARD_VIEW");
        // STEP-04: Assert in the database overrides has 2 item(s), overrides only holds items matching the condition and 1 more.
        await _factory.ExecuteDbAsync(async context =>
        {
            var overrides = await context.UserPermissions
                .Where(item => item.UserId == users.ModId)
                .OrderBy(item => item.PermissionId)
                .ToArrayAsync();
            overrides.Should().HaveCount(2);
            overrides.Should().OnlyContain(item => item.GrantedBy == users.AdminId);
            (await context.AuditLogs.SingleAsync()).Action.Should().Be("MOD_PERMISSIONS_CHANGED");
        });
    }

    // TEST-03: Replace MOD role defaults and persist the exact set with its role audit.
    /// <summary>Covers ADM-P4 replace-all semantics against seeded composite rows and PostgreSQL audit storage.</summary>
    [PostgresEndToEndFact]
    public async Task ReplaceModRolePermissions_ShouldPersistExactDefaultsAndAudit()
    {
        // STEP-01: Reset the test database, create users through SeedAdminAndModAsync and get client from AuthorizedAdminClient.
        await _factory.ResetDatabaseAsync();
        var users = await SeedAdminAndModAsync(addExistingOverride: false);
        using var client = AuthorizedAdminClient(users.AdminId);

        // STEP-02: Send PUT /api/v1/admin/roles/MOD/permissions with a ReplaceModRolePermissionsRequestDto body and keep response.
        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/roles/MOD/permissions",
            new ReplaceModRolePermissionsRequestDto
            {
                PermissionCodes = ["USER_SUSPEND", "DASHBOARD_VIEW"]
            });

        // STEP-03: Obtain data through SuccessDataAsync; assert response.StatusCode is HttpStatusCode.OK, data.Role is "MOD" and 1 more.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var data = await SuccessDataAsync<ReplaceModRolePermissionsResponseDto>(response);
        data.Role.Should().Be("MOD");
        data.PermissionCodes.Should().Equal("USER_SUSPEND", "DASHBOARD_VIEW");
        // STEP-04: Assert in the database codes equals "USER_SUSPEND", audit.Action is "ROLE_PERMISSIONS_CHANGED" and audit.EntityId is 3.
        await _factory.ExecuteDbAsync(async context =>
        {
            var codes = await context.RolePermissions
                .Where(item => item.Role.Code == "MOD")
                .OrderBy(item => item.PermissionId)
                .Select(item => item.Permission.Code)
                .ToArrayAsync();
            codes.Should().Equal("USER_SUSPEND", "DASHBOARD_VIEW");
            var audit = await context.AuditLogs.SingleAsync();
            audit.Action.Should().Be("ROLE_PERMISSIONS_CHANGED");
            audit.EntityId.Should().Be(3);
        });
    }

    /// <summary>Seeds active ADMIN and MOD principals plus an optional existing override.</summary>
    private async Task<(int AdminId, int ModId)> SeedAdminAndModAsync(bool addExistingOverride)
    {
        return await _factory.ExecuteDbAsync(async context =>
        {
            var verifiedAt = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);
            var admin = new User
            {
                Email = "admin-permissions@integration.test",
                RoleId = 4,
                DisplayName = "Permission Admin",
                Status = UserStatus.Active,
                EmailVerifiedAt = verifiedAt
            };
            var mod = new User
            {
                Email = "mod-permissions@integration.test",
                RoleId = 3,
                DisplayName = "Permission Mod",
                Status = UserStatus.Active,
                EmailVerifiedAt = verifiedAt
            };
            context.Users.AddRange(admin, mod);
            await context.SaveChangesAsync();

            if (addExistingOverride)
            {
                var userBanId = await context.Permissions
                    .Where(item => item.Code == "USER_BAN")
                    .Select(item => item.Id)
                    .SingleAsync();
                context.UserPermissions.Add(new UserPermission
                {
                    UserId = mod.Id,
                    PermissionId = userBanId,
                    Effect = PermissionEffect.Deny,
                    GrantedBy = admin.Id,
                    GrantedAt = verifiedAt
                });
                await context.SaveChangesAsync();
            }

            return (admin.Id, mod.Id);
        });
    }

    /// <summary>Creates an HTTP client carrying a valid ADMIN access token for the seeded principal.</summary>
    private HttpClient AuthorizedAdminClient(int adminId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(adminId));
        return client;
    }

    /// <summary>Creates an access token matching the PostgreSQL integration host's JWT configuration.</summary>
    private static string Token(int adminId)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(PostgresApiFactory.JwtSecret)),
            SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "ANGI.IntegrationTests",
            "ANGI.IntegrationTests.Client",
            [
                new Claim(JwtRegisteredClaimNames.Sub, adminId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, "admin-permissions@integration.test"),
                new Claim("role", "ADMIN"),
                new Claim("email_verified", "true")
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials));
    }

    /// <summary>Reads a successful API envelope and returns its required data payload.</summary>
    private static async Task<T> SuccessDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }
}
