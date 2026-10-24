using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Exercises ANGI-175 through HTTP, real use cases, repositories, audit, and PostgreSQL.</summary>
public sealed class AdministrationModsEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public AdministrationModsEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Create a MOD and retrieve it through list and detail endpoints.
    /// <summary>Covers ADM-M1, ADM-M2, and ADM-M3 with real persistence, token generation, audit, and email capture.</summary>
    [PostgresEndToEndFact]
    public async Task CreateListAndGetMod_ShouldPersistAndReturnCompleteContracts()
    {
        await _factory.ResetDatabaseAsync();
        var adminId = await SeedAdminAsync();
        using var client = AuthorizedAdminClient(adminId);

        using var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/mods",
            new CreateModRequestDto
            {
                Email = "linh.mod@angi.vn",
                DisplayName = "Mod Linh",
                PermissionOverrides =
                [
                    new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = "deny" }
                ]
            });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());
        var created = await SuccessDataAsync<AdminUserCardDto>(createResponse);
        created.Role.Should().Be("MOD");
        created.Status.Should().Be("active");
        _factory.Email.ResetToken("linh.mod@angi.vn").Should().NotBeNullOrWhiteSpace();

        using var listResponse = await client.GetAsync("/api/v1/admin/mods?q=linh&status=active&page=1&pageSize=1");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await SuccessDataAsync<Paged<AdminUserCardDto>>(listResponse);
        page.TotalItems.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(created.Id);

        using var detailResponse = await client.GetAsync($"/api/v1/admin/mods/{created.Id}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await SuccessDataAsync<ModDetailDto>(detailResponse);
        detail.Permissions.Overrides.Should().ContainSingle(item =>
            item.PermissionCode == "USER_BAN" && item.Effect == "deny");
        detail.Handled.Should().BeEquivalentTo(new { Verifications = 0, Menus = 0, Reports = 0 });

        await _factory.ExecuteDbAsync(async context =>
        {
            var mod = await context.Users.SingleAsync(user => user.Id == created.Id);
            mod.EmailVerifiedAt.Should().NotBeNull();
            mod.PasswordHash.Should().BeNull();
            (await context.UserTokens.SingleAsync(token => token.UserId == created.Id))
                .Purpose.Should().Be(UserTokenPurpose.PasswordReset);
            (await context.AuditLogs.SingleAsync()).Action.Should().Be("MOD_CREATED");
        });
    }

    // TEST-02: Deactivate a MOD and revoke every active session atomically.
    /// <summary>Covers ADM-M4 row locking, status mutation, session revocation, cache invalidation path, and audit.</summary>
    [PostgresEndToEndFact]
    public async Task DeactivateMod_ShouldRevokeSessionsAndPersistAudit()
    {
        await _factory.ResetDatabaseAsync();
        var users = await SeedAdminAndModWithSessionsAsync();
        using var client = AuthorizedAdminClient(users.AdminId);

        using var response = await client.PatchAsJsonAsync(
            $"/api/v1/admin/mods/{users.ModId}",
            new UpdateModRequestDto { DisplayName = "Deactivated Mod", Status = "deactivated" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var updated = await SuccessDataAsync<AdminUserCardDto>(response);
        updated.DisplayName.Should().Be("Deactivated Mod");
        updated.Status.Should().Be("deactivated");
        await _factory.ExecuteDbAsync(async context =>
        {
            (await context.Users.SingleAsync(user => user.Id == users.ModId)).Status
                .Should().Be(UserStatus.Deactivated);
            (await context.UserSessions.Where(session => session.UserId == users.ModId).ToArrayAsync())
                .Should().OnlyContain(session => session.RevokedAt != null);
            (await context.AuditLogs.SingleAsync()).Action.Should().Be("MOD_UPDATED");
        });
    }

    /// <summary>Seeds one active ADMIN principal for authorization and audit attribution.</summary>
    private async Task<int> SeedAdminAsync()
    {
        return await _factory.ExecuteDbAsync(async context =>
        {
            var admin = new User
            {
                Email = "admin-mods@integration.test",
                RoleId = 4,
                DisplayName = "Mods Admin",
                Status = UserStatus.Active,
                EmailVerifiedAt = DateTime.UtcNow
            };
            context.Users.Add(admin);
            await context.SaveChangesAsync();
            return admin.Id;
        });
    }

    /// <summary>Seeds active ADMIN and MOD principals plus two unrevoked MOD sessions.</summary>
    private async Task<(int AdminId, int ModId)> SeedAdminAndModWithSessionsAsync()
    {
        return await _factory.ExecuteDbAsync(async context =>
        {
            var now = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);
            var admin = new User
            {
                Email = "admin-mod-update@integration.test",
                RoleId = 4,
                DisplayName = "Mods Admin",
                Status = UserStatus.Active,
                EmailVerifiedAt = now
            };
            var mod = new User
            {
                Email = "mod-update@integration.test",
                RoleId = 3,
                DisplayName = "Active Mod",
                Status = UserStatus.Active,
                EmailVerifiedAt = now
            };
            context.Users.AddRange(admin, mod);
            context.UserSessions.AddRange(
                new UserSession
                {
                    User = mod,
                    RefreshTokenHash = "session-hash-one",
                    ExpiresAt = now.AddDays(30)
                },
                new UserSession
                {
                    User = mod,
                    RefreshTokenHash = "session-hash-two",
                    ExpiresAt = now.AddDays(30)
                });
            await context.SaveChangesAsync();
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

    /// <summary>Creates an access token matching the PostgreSQL integration host configuration.</summary>
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
                new Claim(JwtRegisteredClaimNames.Email, "admin-mods@integration.test"),
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
