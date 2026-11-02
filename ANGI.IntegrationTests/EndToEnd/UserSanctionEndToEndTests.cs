using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Exercises MOD-U3 and MOD-U4 through real HTTP, the shared account lock and PostgreSQL.</summary>
public sealed class UserSanctionEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with the shared PostgreSQL-backed API host.</summary>
    public UserSanctionEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Suspend a traveler, then unban: status, sanction lift and the returned card (with avatar) persist.
    /// <summary>Both use cases lock the account through the same repository query, which also loads role and avatar.</summary>
    [PostgresEndToEndFact]
    public async Task SuspendThenUnban_ShouldRestoreTheAccountAndLiftTheSanction()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await SeedAsync(deleted: false);
        using var client = _factory.CreateClient();
        Authorize(client, seed.ModId);

        using var suspend = await client.PostAsJsonAsync($"/api/v1/mod/users/{seed.TravelerId}/sanctions",
            new CreateUserSanctionRequestDto { Type = "suspend", DurationDays = 7, Reason = "Spam bình luận" });
        suspend.StatusCode.Should().Be(HttpStatusCode.Created, await suspend.Content.ReadAsStringAsync());

        using var unban = await client.PostAsJsonAsync($"/api/v1/mod/users/{seed.TravelerId}/unban",
            new UnbanUserRequestDto { LiftReason = "Đã xác minh nhầm" });
        unban.StatusCode.Should().Be(HttpStatusCode.OK, await unban.Content.ReadAsStringAsync());
        var card = (await unban.Content.ReadFromJsonAsync<ApiResponse<AdminUserCardDto>>())!.Data!;
        card.Status.Should().Be("active");
        card.SuspendedUntil.Should().BeNull();
        card.Role.Should().Be("TRAVELER");
        card.AvatarUrl.Should().Be("https://cdn.test/avatars/traveler.jpg");

        await _factory.ExecuteDbAsync(async context =>
        {
            var user = await context.Users.AsNoTracking().SingleAsync(u => u.Id == seed.TravelerId);
            user.Status.Should().Be(UserStatus.Active);
            user.SuspendedUntil.Should().BeNull();
            var sanction = await context.UserSanctions.AsNoTracking().SingleAsync(s => s.UserId == seed.TravelerId);
            sanction.LiftedBy.Should().Be(seed.ModId);
            sanction.LiftedAt.Should().NotBeNull();
            sanction.LiftReason.Should().Be("Đã xác minh nhầm");
        });

        using var again = await client.PostAsJsonAsync($"/api/v1/mod/users/{seed.TravelerId}/unban",
            new UnbanUserRequestDto { LiftReason = "Lần hai" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("USER_NOT_SANCTIONED");
    }

    // TEST-02: A soft-deleted account is USER_NOT_FOUND for both sanction and unban, like MOD-U2.
    /// <summary>The account lock skips deleted rows, so neither use case can change a deleted account.</summary>
    [PostgresEndToEndFact]
    public async Task SanctionOrUnban_OnDeletedAccount_ShouldReturnUserNotFound()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await SeedAsync(deleted: true);
        using var client = _factory.CreateClient();
        Authorize(client, seed.ModId);

        using var sanction = await client.PostAsJsonAsync($"/api/v1/mod/users/{seed.TravelerId}/sanctions",
            new CreateUserSanctionRequestDto { Type = "ban", Reason = "Vi phạm nghiêm trọng" });
        using var unban = await client.PostAsJsonAsync($"/api/v1/mod/users/{seed.TravelerId}/unban",
            new UnbanUserRequestDto { LiftReason = "Gỡ cấm" });

        foreach (var response in new[] { sanction, unban })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("USER_NOT_FOUND");
        }

        await _factory.ExecuteDbAsync(async context =>
            (await context.UserSanctions.CountAsync(s => s.UserId == seed.TravelerId)).Should().Be(0));
    }

    private sealed record SeedResult(int ModId, int TravelerId);

    /// <summary>Seeds one moderator and one traveler with an avatar; a deleted traveler is banned, so only deletion blocks it.</summary>
    private Task<SeedResult> SeedAsync(bool deleted) => _factory.ExecuteDbAsync(async context =>
    {
        var roleIds = await context.Roles.ToDictionaryAsync(role => role.Code, role => role.Id);
        var mod = new User
        {
            Email = $"mod-{Guid.NewGuid():N}@integration.test",
            RoleId = roleIds["MOD"],
            DisplayName = "Sanction Mod",
            Status = UserStatus.Active,
            EmailVerifiedAt = DateTime.UtcNow
        };
        var traveler = new User
        {
            Email = $"traveler-{Guid.NewGuid():N}@integration.test",
            RoleId = roleIds["TRAVELER"],
            DisplayName = "Sanctioned Traveler",
            Status = deleted ? UserStatus.Banned : UserStatus.Active,
            EmailVerifiedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null,
            AvatarMedia = new MediaFile
            {
                StorageKey = $"image/upload/avatar/{Guid.NewGuid():N}.jpg",
                Url = "https://cdn.test/avatars/traveler.jpg",
                MimeType = "image/jpeg"
            }
        };
        context.Users.AddRange(mod, traveler);
        await context.SaveChangesAsync();
        return new SeedResult(mod.Id, traveler.Id);
    });

    /// <summary>Signs a MOD access token with the test host's key.</summary>
    private static void Authorize(HttpClient client, int userId)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(PostgresApiFactory.JwtSecret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            "ANGI.IntegrationTests",
            "ANGI.IntegrationTests.Client",
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, "staff@integration.test"),
                new Claim("role", "MOD"),
                new Claim("email_verified", "true")
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
    }
}
