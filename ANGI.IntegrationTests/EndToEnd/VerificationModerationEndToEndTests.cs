using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Exercises ANGI-178 through real HTTP, use cases, EF Core, PostgreSQL locks, and persisted side effects.</summary>
public sealed class VerificationModerationEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with the shared PostgreSQL-backed API host.</summary>
    public VerificationModerationEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: List, inspect, and request more information while persisting every documented MOD-V3 side effect.
    /// <summary>Verifies MOD-V1 through MOD-V3 as one real PostgreSQL workflow, including signed URLs and permission denial.</summary>
    [PostgresEndToEndFact]
    public async Task ReviewVerificationWorkflow_ShouldPersistDecisionNotificationAndAudit()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await SeedAsync();
        using var client = _factory.CreateClient();
        Authorize(client, seed.ModId, "MOD");

        using var listResponse = await client.GetAsync(
            "/api/v1/mod/verifications?status=pending&province=Da%20Nang&page=1&pageSize=20");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await SuccessDataAsync<PagedResponse<ModVerificationCardDto>>(listResponse);
        page.Items.Should().ContainSingle(item => item.Id == seed.VerificationId);
        page.Items[0].Documents[0].Url.Should().StartWith("https://media.integration/signed/");

        using var detailResponse = await client.GetAsync($"/api/v1/mod/verifications/{seed.VerificationId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await SuccessDataAsync<VerificationReviewDetailDto>(detailResponse);
        detail.Id.Should().Be(seed.VerificationId);
        detail.Restaurant.Id.Should().Be(seed.RestaurantId);
        detail.Previous!.Id.Should().Be(seed.PreviousVerificationId);

        using var decisionResponse = await client.PostAsJsonAsync(
            $"/api/v1/mod/verifications/{seed.VerificationId}/decision",
            new ReviewVerificationRequestDto
            {
                Decision = "needs_more_info",
                ReviewNote = "Please provide a current food safety certificate."
            });
        decisionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SuccessDataAsync<VerificationDto>(decisionResponse)).Status.Should().Be("needs_more_info");

        await _factory.ExecuteDbAsync(async context =>
        {
            var verification = await context.RestaurantVerifications.SingleAsync(item => item.Id == seed.VerificationId);
            verification.Status.Should().Be(VerificationStatus.NeedsMoreInfo);
            verification.ReviewedBy.Should().Be(seed.ModId);
            verification.ReviewedAt.Should().NotBeNull();
            verification.ReviewNote.Should().Be("Please provide a current food safety certificate.");
            (await context.Restaurants.SingleAsync(item => item.Id == seed.RestaurantId))
                .VerificationStatus.Should().Be(RestaurantVerificationStatus.Rejected);
            (await context.Notifications.SingleAsync(item => item.RecipientId == seed.OwnerId))
                .Type.Should().Be("VERIFICATION_NEEDS_MORE_INFO");
            (await context.AuditLogs.SingleAsync(item => item.EntityId == seed.RestaurantId))
                .Action.Should().Be(AuditActions.RestaurantVerificationRejected);
        });

        using var repeatedResponse = await client.PostAsJsonAsync(
            $"/api/v1/mod/verifications/{seed.VerificationId}/decision",
            new { decision = "approve" });
        repeatedResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await AssertErrorAsync(repeatedResponse, "VERIFICATION_ALREADY_REVIEWED");

        await DenyVerificationPermissionAsync(seed.ModId);
        using var deniedResponse = await client.GetAsync("/api/v1/mod/verifications");
        deniedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertErrorAsync(deniedResponse, "FORBIDDEN");
    }

    // TEST-02: Serialize two simultaneous decisions so exactly one succeeds and one receives the documented conflict.
    /// <summary>Verifies the PostgreSQL row lock prevents double review, duplicate notifications, and duplicate audits.</summary>
    [PostgresEndToEndFact]
    public async Task Decide_WhenRequestsRace_ShouldCommitExactlyOnce()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await SeedAsync();
        using var firstClient = _factory.CreateClient();
        using var secondClient = _factory.CreateClient();
        Authorize(firstClient, seed.ModId, "MOD");
        Authorize(secondClient, seed.ModId, "MOD");
        var path = $"/api/v1/mod/verifications/{seed.VerificationId}/decision";

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync(path, new { decision = "approve" }),
            secondClient.PostAsJsonAsync(path, new { decision = "approve" }));

        responses.Select(response => response.StatusCode)
            .Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        await _factory.ExecuteDbAsync(async context =>
        {
            (await context.Notifications.CountAsync(item => item.RecipientId == seed.OwnerId)).Should().Be(1);
            (await context.AuditLogs.CountAsync(item => item.EntityId == seed.RestaurantId)).Should().Be(1);
            (await context.RestaurantVerifications.SingleAsync(item => item.Id == seed.VerificationId))
                .Status.Should().Be(VerificationStatus.Approved);
        });
    }

    // TEST-03: Reject Admin access at HTTP and database permission boundaries without changing verification data.
    /// <summary>Verifies every endpoint rejects Admin and a stale MOD claim cannot bypass the current database role.</summary>
    [PostgresEndToEndFact]
    public async Task AdminAccess_ShouldReturnForbiddenWithoutSideEffects()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await SeedAsync();
        var adminId = await _factory.ExecuteDbAsync(async context =>
        {
            var admin = new User
            {
                Email = $"admin-{Guid.NewGuid():N}@integration.test",
                RoleId = await context.Roles.Where(role => role.Code == "ADMIN").Select(role => role.Id).SingleAsync(),
                DisplayName = "Verification Admin",
                Status = UserStatus.Active,
                EmailVerifiedAt = DateTime.UtcNow
            };
            context.Users.Add(admin);
            await context.SaveChangesAsync();
            return admin.Id;
        });
        using var client = _factory.CreateClient();
        foreach (var claimRole in new[] { "ADMIN", "MOD" })
        {
            Authorize(client, adminId, claimRole);
            using var list = await client.GetAsync("/api/v1/mod/verifications");
            using var detail = await client.GetAsync($"/api/v1/mod/verifications/{seed.VerificationId}");
            using var decision = await client.PostAsJsonAsync(
                $"/api/v1/mod/verifications/{seed.VerificationId}/decision", new { decision = "approve" });

            foreach (var response in new[] { list, detail, decision })
            {
                response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                await AssertErrorAsync(response, "FORBIDDEN");
            }
        }

        await _factory.ExecuteDbAsync(async context =>
        {
            var verification = await context.RestaurantVerifications.SingleAsync(item => item.Id == seed.VerificationId);
            verification.Status.Should().Be(VerificationStatus.Pending);
            verification.ReviewedBy.Should().BeNull();
            (await context.Notifications.CountAsync()).Should().Be(0);
            (await context.AuditLogs.CountAsync()).Should().Be(0);
        });
    }

    /// <summary>Seeds active owner/Mod accounts, a pending restaurant, linked submissions, and one private document.</summary>
    private async Task<SeedResult> SeedAsync()
    {
        return await _factory.ExecuteDbAsync(async context =>
        {
            var ownerRoleId = await context.Roles.Where(role => role.Code == "RESTAURANT_OWNER").Select(role => role.Id).SingleAsync();
            var modRoleId = await context.Roles.Where(role => role.Code == "MOD").Select(role => role.Id).SingleAsync();
            var owner = new User
            {
                Email = $"owner-{Guid.NewGuid():N}@integration.test",
                RoleId = ownerRoleId,
                DisplayName = "Verification Owner",
                Status = UserStatus.Active,
                EmailVerifiedAt = DateTime.UtcNow
            };
            var mod = new User
            {
                Email = $"mod-{Guid.NewGuid():N}@integration.test",
                RoleId = modRoleId,
                DisplayName = "Verification Mod",
                Status = UserStatus.Active,
                EmailVerifiedAt = DateTime.UtcNow
            };
            context.Users.AddRange(owner, mod);
            await context.SaveChangesAsync();

            var media = new MediaFile
            {
                UploaderId = owner.Id,
                StorageKey = $"raw/authenticated/verification_doc/{Guid.NewGuid():N}",
                Url = "private",
                MimeType = "application/pdf"
            };
            var restaurant = new Domain.Entities.Restaurant
            {
                OwnerId = owner.Id,
                Name = "Verification Integration Restaurant",
                Slug = $"verification-{Guid.NewGuid():N}",
                Phone = "0900000000",
                AddressLine = "1 Integration Street",
                District = "Hai Chau",
                ProvinceName = "Da Nang",
                Latitude = 16.067m,
                Longitude = 108.220m,
                VerificationStatus = RestaurantVerificationStatus.Pending
            };
            context.AddRange(media, restaurant);
            await context.SaveChangesAsync();

            var previous = new RestaurantVerification
            {
                RestaurantId = restaurant.Id,
                SubmittedBy = owner.Id,
                LegalName = "Previous Integration Company",
                BusinessLicenseNo = "OLD-001",
                Status = VerificationStatus.NeedsMoreInfo,
                ReviewedBy = mod.Id,
                ReviewedAt = DateTime.UtcNow.AddDays(-2),
                ReviewNote = "Previous document was incomplete.",
                SubmittedAt = DateTime.UtcNow.AddDays(-3)
            };
            context.RestaurantVerifications.Add(previous);
            await context.SaveChangesAsync();

            var current = new RestaurantVerification
            {
                RestaurantId = restaurant.Id,
                SubmittedBy = owner.Id,
                PreviousId = previous.Id,
                LegalName = "Current Integration Company",
                BusinessLicenseNo = "NEW-001",
                Status = VerificationStatus.Pending,
                SubmittedAt = DateTime.UtcNow.AddDays(-1),
                Documents =
                [
                    new RestaurantVerificationDocument
                    {
                        DocType = VerificationDocumentType.BusinessLicense,
                        MediaId = media.Id
                    }
                ]
            };
            context.RestaurantVerifications.Add(current);
            await context.SaveChangesAsync();
            return new SeedResult(owner.Id, mod.Id, restaurant.Id, previous.Id, current.Id);
        });
    }

    /// <summary>Adds a user-level deny override for RESTAURANT_VERIFY.</summary>
    private async Task DenyVerificationPermissionAsync(int modId)
    {
        await _factory.ExecuteDbAsync(async context =>
        {
            var permissionId = await context.Permissions
                .Where(permission => permission.Code == "RESTAURANT_VERIFY")
                .Select(permission => permission.Id)
                .SingleAsync();
            context.UserPermissions.Add(new UserPermission
            {
                UserId = modId,
                PermissionId = permissionId,
                Effect = PermissionEffect.Deny,
                GrantedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        });
    }

    /// <summary>Adds a valid signed bearer token to subsequent requests.</summary>
    private static void Authorize(HttpClient client, int userId, string role)
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
                new Claim("role", role),
                new Claim("email_verified", "true")
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
    }

    /// <summary>Reads and returns the required data from a successful API envelope.</summary>
    private static async Task<T> SuccessDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }

    /// <summary>Asserts the canonical error envelope code.</summary>
    private static async Task AssertErrorAsync(HttpResponseMessage response, string errorCode)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
    }

    private sealed record SeedResult(
        int OwnerId,
        int ModId,
        long RestaurantId,
        long PreviousVerificationId,
        long VerificationId);

    private sealed class PagedResponse<T>
    {
        public IReadOnlyList<T> Items { get; set; } = [];
        public int Page { get; set; }
        public int PageSize { get; set; }
        public long TotalItems { get; set; }
        public int TotalPages { get; set; }
    }
}
