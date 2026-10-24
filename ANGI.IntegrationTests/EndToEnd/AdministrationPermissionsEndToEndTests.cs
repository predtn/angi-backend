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
        await _factory.ResetDatabaseAsync();
        var users = await SeedAdminAndModAsync(addExistingOverride: true);
        using var client = AuthorizedAdminClient(users.AdminId);

        using var catalogResponse = await client.GetAsync("/api/v1/admin/permissions");
        using var modResponse = await client.GetAsync($"/api/v1/admin/mods/{users.ModId}/permissions");

        catalogResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        modResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var catalog = await SuccessDataAsync<PermissionCatalogDto>(catalogResponse);
        var mod = await SuccessDataAsync<ModPermissionsDto>(modResponse);
        catalog.Permissions.Should().HaveCount(13);
        catalog.RoleDefaults.Mod.Should().HaveCount(10);
        catalog.RoleDefaults.Admin.Should().Equal("*");
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
        await _factory.ResetDatabaseAsync();
        var users = await SeedAdminAndModAsync(addExistingOverride: true);
        using var client = AuthorizedAdminClient(users.AdminId);

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

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var data = await SuccessDataAsync<ModPermissionsDto>(response);
        data.Effective.Should().NotContain("USER_BAN");
        data.Effective.Should().Contain("DASHBOARD_VIEW");
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
        await _factory.ResetDatabaseAsync();
        var users = await SeedAdminAndModAsync(addExistingOverride: false);
        using var client = AuthorizedAdminClient(users.AdminId);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/roles/MOD/permissions",
            new ReplaceModRolePermissionsRequestDto
            {
                PermissionCodes = ["USER_SUSPEND", "DASHBOARD_VIEW"]
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var data = await SuccessDataAsync<ReplaceModRolePermissionsResponseDto>(response);
        data.Role.Should().Be("MOD");
        data.PermissionCodes.Should().Equal("USER_SUSPEND", "DASHBOARD_VIEW");
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
