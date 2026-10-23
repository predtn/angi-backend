using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.IntegrationTests.WebApi.Controllers.Moderation;

/// <summary>Exercises all ANGI-191 endpoints through the production HTTP pipeline and PostgreSQL persistence.</summary>
public sealed class RestaurantModerationHttpTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private sealed record World(long RestaurantId, long HiddenRestaurantId, long DeletedRestaurantId,
        int DishId, int RemovedDishId, long ReportId, long RelatedReportId, long BlogReportId,
        int OwnerId, int ModId, int AdminId, int TravelerId, long EvidenceId);

    // TEST-01: List hidden and unverified restaurants, apply every filter, exclude soft-deleted rows and paginate safely.
    /// <summary>Checks MOD-R1 query translation, all filter partitions and integer offset boundaries.</summary>
    [PostgresEndToEndFact]
    public async Task ListsAndFiltersModeratorRestaurants()
    {
        var world = await SeedAsync();
        using var client = await ClientAsync(world.ModId);
        var all = await PageAsync(client, "");
        all.TotalItems.Should().Be(2);
        all.Items.Select(x => x.Id).Should().BeEquivalentTo([world.RestaurantId, world.HiddenRestaurantId]);
        all.Items.Single(x => x.Id == world.RestaurantId).PendingReportCount.Should().Be(2);
        all.Items.Single(x => x.Id == world.RestaurantId).IsOpenNow.Should().BeTrue();
        foreach (var query in new[]
        {
            "?q=BÚN", "?province=Hue", "?verificationStatus=verified", "?operatingStatus=open",
            "?moderationStatus=visible", "?hasPendingReports=true",
            "?q=bún&province=Hue&verificationStatus=verified&operatingStatus=open&moderationStatus=visible&hasPendingReports=true"
        }) (await PageAsync(client, query)).Items.Should().ContainSingle(x => x.Id == world.RestaurantId);
        foreach (var query in new[] { "?hasPendingReports=false", "?moderationStatus=hidden", "?verificationStatus=unverified",
            "?operatingStatus=temporarily_closed" })
            (await PageAsync(client, query)).Items.Should().ContainSingle(x => x.Id == world.HiddenRestaurantId);
        (await PageAsync(client, "?q=no-such-name")).TotalPages.Should().Be(0);
        (await PageAsync(client, "?page=1&pageSize=1")).Items.Should().HaveCount(1);
        (await PageAsync(client, "?page=2&pageSize=1")).Items.Should().HaveCount(1);
        (await PageAsync(client, "?page=3&pageSize=1")).Items.Should().BeEmpty();
        (await PageAsync(client, "?page=2147483647&pageSize=100")).Items.Should().BeEmpty();
        foreach (var query in new[] { "?page=0", "?pageSize=0", "?pageSize=101", "?verificationStatus=0", "?moderationStatus=VISIBLE",
            "?operatingStatus=invalid", "?hasPendingReports=invalid" })
            await ErrorAsync(await client.GetAsync("/api/v1/mod/restaurants" + query), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    // TEST-02: Return the full owner profile, hidden menu dishes, signed evidence and only twenty newest report/audit entries.
    /// <summary>Checks MOD-R2 serialization, history bounds, private media and exclusion of removed menu dishes.</summary>
    [PostgresEndToEndFact]
    public async Task ReturnsCompleteBoundedDetail()
    {
        var world = await SeedAsync();
        await factory.ExecuteDbAsync(async context =>
        {
            for (var i = 0; i < 22; i++)
            {
                context.Reports.Add(new Report { ReporterId = world.TravelerId, RestaurantId = world.RestaurantId,
                    TargetType = ReportTargetType.Restaurant, ReasonCode = ReportReasonCode.Other, Status = ReportStatus.Dismissed });
                context.AuditLogs.Add(new AuditLog { ActorId = world.ModId, ActorRole = "MOD", SubjectUserId = world.OwnerId,
                    EntityType = AuditEntityTypes.Restaurant, EntityId = world.RestaurantId, Action = AuditActions.RestaurantUpdated });
            }
            await context.SaveChangesAsync();
        });
        using var client = await ClientAsync(world.ModId);
        var detail = await DataAsync<ModRestaurantDetailDto>(await client.GetAsync($"/api/v1/mod/restaurants/{world.RestaurantId}"));
        detail.Owner.Id.Should().Be(world.OwnerId);
        detail.Owner.Role.Should().Be("RESTAURANT_OWNER");
        detail.Dishes.Should().HaveCount(2).And.Contain(x => x.Status == "hidden");
        detail.Dishes.Should().NotContain(x => x.Id == world.RemovedDishId);
        detail.BusinessHours.Should().HaveCount(7);
        detail.Reports.Should().HaveCount(20);
        detail.Reports.Select(x => x.Id).Should().BeInDescendingOrder();
        detail.RecentAudit.Should().HaveCount(20);
        detail.RecentAudit.Select(x => x.Id).Should().BeInDescendingOrder();
        detail.RecentAudit.Should().OnlyContain(x => x.Actor!.Role == "MOD" && x.SubjectUser!.Id == world.OwnerId);
        var json = await (await client.GetAsync($"/api/v1/mod/restaurants/{world.RestaurantId}")).Content.ReadAsStringAsync();
        json.Should().NotContain("passwordHash").And.NotContain("refreshTokenHash").And.NotContain("storageKey");
        // Read the older evidence report through the resolution response to also validate signed evidence delivery.
        var report = await DataAsync<ReportDto>(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve",
            new ResolveReportRequestDto { Outcome = "resolved", ResolutionAction = "none", ApplyToRelatedReports = false }));
        report.EvidenceUrls.Should().ContainSingle(x => x.StartsWith("https://media.integration/signed/"));
        report.Reporter.Id.Should().Be(world.TravelerId);
        report.HandledBy!.Id.Should().Be(world.ModId);
        report.ReportCountOnTarget.Should().Be(1);
        foreach (var id in new[] { world.DeletedRestaurantId, long.MaxValue })
            await ErrorAsync(await client.GetAsync($"/api/v1/mod/restaurants/{id}"), HttpStatusCode.NotFound, "RESTAURANT_NOT_FOUND");
    }

    // TEST-03: Persist each restaurant moderation state with audit and owner notification, without restaurant reco events.
    /// <summary>Checks MOD-R3 state transitions, repeat idempotency and report association validation.</summary>
    [PostgresEndToEndFact]
    public async Task ChangesRestaurantStateAtomically()
    {
        var world = await SeedAsync();
        using var client = await ClientAsync(world.ModId);
        var initialOutbox = await factory.ExecuteDbAsync(context => context.OutboxMessages.CountAsync());
        foreach (var status in new[] { "hidden", "suspended", "visible" })
        {
            var result = await DataAsync<UpdateRestaurantModerationStatusResponseDto>(await client.PutAsJsonAsync(
                $"/api/v1/mod/restaurants/{world.RestaurantId}/moderation-status",
                new { moderationStatus = status, reason = " reason ", reportId = world.ReportId }));
            result.ModerationStatus.Should().Be(status);
        }
        await DataAsync<UpdateRestaurantModerationStatusResponseDto>(await client.PutAsJsonAsync(
            $"/api/v1/mod/restaurants/{world.RestaurantId}/moderation-status", new { moderationStatus = "visible", reason = "repeat" }));
        await factory.ExecuteDbAsync(async context =>
        {
            (await context.Restaurants.SingleAsync(r => r.Id == world.RestaurantId)).ModerationStatus.Should().Be(RestaurantModerationStatus.Visible);
            var logs = await context.AuditLogs.OrderBy(x => x.Id).ToListAsync();
            logs.Select(x => x.Action).Should().Equal("RESTAURANT_HIDDEN", "RESTAURANT_SUSPENDED", "RESTAURANT_SHOWN");
            logs.Should().OnlyContain(x => x.ActorId == world.ModId && x.SubjectUserId == world.OwnerId);
            JsonDocument.Parse(logs[0].NewValues!).RootElement.GetProperty("reason").GetString().Should().Be("reason");
            (await context.Notifications.CountAsync(n => n.RecipientId == world.OwnerId)).Should().Be(3);
            (await context.OutboxMessages.CountAsync()).Should().Be(initialOutbox);
        });
        await ErrorAsync(await client.PutAsJsonAsync($"/api/v1/mod/restaurants/{world.RestaurantId}/moderation-status",
            new { moderationStatus = "hidden", reason = "reason", reportId = world.BlogReportId }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ErrorAsync(await client.PutAsJsonAsync($"/api/v1/mod/restaurants/{long.MaxValue}/moderation-status",
            new { moderationStatus = "hidden", reason = "reason" }), HttpStatusCode.NotFound, "RESTAURANT_NOT_FOUND");
    }

    // TEST-04: Hide and restore a dish with trigger-generated outbox events; reject removed and missing dishes.
    /// <summary>Checks MOD-R4 against real database triggers including repeated-state event suppression.</summary>
    [PostgresEndToEndFact]
    public async Task ChangesDishStateAndCreatesCorrectOutboxEvents()
    {
        var world = await SeedAsync();
        using var client = await ClientAsync(world.ModId);
        foreach (var status in new[] { "hidden", "hidden", "active" })
            (await DataAsync<UpdateDishModerationStatusResponseDto>(await client.PutAsJsonAsync(
                $"/api/v1/mod/dishes/{world.DishId}/moderation-status", new { status, reason = "reason" }))).Status.Should().Be(status);
        await factory.ExecuteDbAsync(async context =>
        {
            var events = await context.OutboxMessages.Where(m => m.AggregateKey == $"dish:{world.DishId}").OrderBy(m => m.Id).ToListAsync();
            events.Select(x => x.EventType).Should().Equal(OutboxEventType.DishUpserted, OutboxEventType.DishDeactivated, OutboxEventType.DishUpserted);
            (await context.Dishes.SingleAsync(d => d.Id == world.DishId)).Status.Should().Be(DishStatus.Active);
        });
        foreach (var status in new[] { "hidden", "active" })
            await ErrorAsync(await client.PutAsJsonAsync($"/api/v1/mod/dishes/{world.RemovedDishId}/moderation-status",
                new { status, reason = "reason" }), HttpStatusCode.Conflict, "DISH_REMOVED");
        await ErrorAsync(await client.PutAsJsonAsync($"/api/v1/mod/dishes/{int.MaxValue}/moderation-status",
            new { status = "hidden", reason = "reason" }), HttpStatusCode.NotFound, "DISH_NOT_FOUND");
    }

    // TEST-05: Resolve related restaurant reports and apply the action in one transaction; reject closed retries.
    /// <summary>Checks related report defaults, notification recipients, report count and action-specific audit entries.</summary>
    [PostgresEndToEndFact]
    public async Task ResolvesRelatedReportsAndAppliesRestaurantAction()
    {
        var world = await SeedAsync();
        using var client = await ClientAsync(world.ModId);
        var response = await DataAsync<ReportDto>(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve",
            new { outcome = "resolved", resolutionAction = "hide_restaurant", resolutionNote = "Confirmed" }));
        response.Status.Should().Be("resolved");
        response.ReportCountOnTarget.Should().Be(0);
        await factory.ExecuteDbAsync(async context =>
        {
            (await context.Reports.Where(r => r.RestaurantId == world.RestaurantId).ToListAsync()).Should()
                .OnlyContain(r => r.Status == ReportStatus.Resolved && r.HandledBy == world.ModId);
            (await context.Restaurants.SingleAsync(r => r.Id == world.RestaurantId)).ModerationStatus.Should().Be(RestaurantModerationStatus.Hidden);
            (await context.AuditLogs.CountAsync(a => a.Action == AuditActions.ReportResolved)).Should().Be(2);
            (await context.AuditLogs.CountAsync(a => a.Action == AuditActions.RestaurantHidden)).Should().Be(1);
            (await context.Notifications.Where(n => n.Type == "REPORT_RESOLVED").Select(n => n.RecipientId).ToListAsync())
                .Should().BeEquivalentTo([world.TravelerId, world.AdminId, world.OwnerId]);
        });
        await ErrorAsync(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.RelatedReportId}/resolve",
            new { outcome = "resolved", resolutionAction = "suspend_restaurant" }), HttpStatusCode.Conflict, "REPORT_CLOSED");
    }

    // TEST-06: Dismiss a single report while leaving other open reports untouched; enforce target/action compatibility.
    /// <summary>Checks related=false, dismissed outcomes and ACTION_NOT_ALLOWED_FOR_TARGET responses.</summary>
    [PostgresEndToEndFact]
    public async Task DismissesOnlySelectedReportAndRejectsWrongTargetActions()
    {
        var world = await SeedAsync();
        using var client = await ClientAsync(world.ModId);
        foreach (var action in new[] { "hide_blog", "remove_blog" })
            await ErrorAsync(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve",
                new { outcome = "resolved", resolutionAction = action }), HttpStatusCode.BadRequest, "ACTION_NOT_ALLOWED_FOR_TARGET");
        foreach (var action in new[] { "hide_restaurant", "suspend_restaurant" })
            await ErrorAsync(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.BlogReportId}/resolve",
                new { outcome = "resolved", resolutionAction = action }), HttpStatusCode.BadRequest, "ACTION_NOT_ALLOWED_FOR_TARGET");
        var result = await DataAsync<ReportDto>(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve",
            new { outcome = "dismissed", resolutionAction = "none", applyToRelatedReports = false }));
        result.Status.Should().Be("dismissed");
        result.ReportCountOnTarget.Should().Be(1);
        await factory.ExecuteDbAsync(async context =>
            (await context.Reports.SingleAsync(r => r.Id == world.RelatedReportId)).Status.Should().Be(ReportStatus.InReview));
        await ErrorAsync(await client.PostAsJsonAsync($"/api/v1/mod/reports/{long.MaxValue}/resolve",
            new { outcome = "resolved", resolutionAction = "none" }), HttpStatusCode.NotFound, "REPORT_NOT_FOUND");
    }

    // TEST-07: Apply warn, suspend and ban to the restaurant owner, preserving UTC deadlines and revoking sessions when needed.
    /// <summary>Checks user sanctions, account-status cache invalidation, audit records and session persistence.</summary>
    [PostgresEndToEndFact]
    public async Task AppliesAllAccountSanctions()
    {
        foreach (var action in new[] { "warn_user", "suspend_user", "ban_user" })
        {
            var world = await SeedAsync();
            using var owner = await ClientAsync(world.OwnerId);
            (await owner.GetAsync("/api/v1/me")).EnsureSuccessStatusCode();
            using var client = await ClientAsync(world.ModId);
            var until = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds()).ToOffset(TimeSpan.FromHours(7));
            await DataAsync<ReportDto>(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve",
                new { outcome = "resolved", resolutionAction = action, resolutionNote = "Violation", suspendUntil = until }));
            await factory.ExecuteDbAsync(async context =>
            {
                var user = await context.Users.SingleAsync(u => u.Id == world.OwnerId);
                user.Status.Should().Be(action == "warn_user" ? UserStatus.Active : action == "ban_user" ? UserStatus.Banned : UserStatus.Suspended);
                var sanction = await context.UserSanctions.SingleAsync();
                sanction.ReportId.Should().Be(world.ReportId);
                sanction.IssuedBy.Should().Be(world.ModId);
                sanction.EndsAt.Should().Be(action == "suspend_user" ? until.UtcDateTime : null);
                var session = await context.UserSessions.SingleAsync(s => s.UserId == world.OwnerId);
                session.RevokedAt.HasValue.Should().Be(action != "warn_user");
                (await context.AuditLogs.CountAsync(a => a.EntityType == AuditEntityTypes.User)).Should().Be(1);
            });
            if (action != "warn_user") await ErrorAsync(await owner.GetAsync("/api/v1/me"), HttpStatusCode.Forbidden,
                action == "ban_user" ? "ACCOUNT_BANNED" : "ACCOUNT_SUSPENDED");
        }
    }

    // TEST-08: Require authentication, a staff role, the effective restaurant permission and target-specific report permissions.
    /// <summary>Exercises deny overrides, read permission alternatives, Admin bypass and sanction privilege boundaries.</summary>
    [PostgresEndToEndFact]
    public async Task EnforcesRolesAndEffectivePermissions()
    {
        var world = await SeedAsync();
        using var guest = factory.CreateClient();
        using var traveler = await ClientAsync(world.TravelerId);
        await ErrorAsync(await guest.GetAsync("/api/v1/mod/restaurants"), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        await ErrorAsync(await traveler.GetAsync("/api/v1/mod/restaurants"), HttpStatusCode.Forbidden, "FORBIDDEN");
        using var mod = await ClientAsync(world.ModId);
        await DenyAsync(world.ModId, "RESTAURANT_MODERATE");
        (await mod.GetAsync("/api/v1/mod/restaurants")).EnsureSuccessStatusCode();
        (await mod.GetAsync($"/api/v1/mod/restaurants/{world.RestaurantId}")).EnsureSuccessStatusCode();
        await ErrorAsync(await mod.PutAsJsonAsync($"/api/v1/mod/restaurants/{world.RestaurantId}/moderation-status",
            new { moderationStatus = "hidden", reason = "reason" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ErrorAsync(await mod.PutAsJsonAsync($"/api/v1/mod/dishes/{world.DishId}/moderation-status",
            new { status = "hidden", reason = "reason" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await DenyAsync(world.ModId, "RESTAURANT_VERIFY");
        await ErrorAsync(await mod.GetAsync("/api/v1/mod/restaurants"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await DenyAsync(world.ModId, "REPORT_RESTAURANT_HANDLE");
        await ErrorAsync(await mod.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve",
            new { outcome = "resolved", resolutionAction = "none" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await DenyAsync(world.ModId, "USER_BAN");
        await ErrorAsync(await mod.PostAsJsonAsync($"/api/v1/mod/reports/{world.BlogReportId}/resolve",
            new { outcome = "resolved", resolutionAction = "ban_user" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        using var admin = await ClientAsync(world.AdminId);
        (await admin.GetAsync("/api/v1/mod/restaurants")).EnsureSuccessStatusCode();
        await DataAsync<UpdateRestaurantModerationStatusResponseDto>(await admin.PutAsJsonAsync(
            $"/api/v1/mod/restaurants/{world.RestaurantId}/moderation-status", new { moderationStatus = "hidden", reason = "Admin" }));
    }

    // TEST-09: Reject missing, null, malformed and overlong request fields using the standard validation envelope.
    /// <summary>Checks all mutation routes and Vietnamese field errors through real model binding.</summary>
    [PostgresEndToEndFact]
    public async Task RejectsInvalidBodiesAndSuspensionDeadlines()
    {
        var world = await SeedAsync();
        using var client = await ClientAsync(world.ModId);
        foreach (var payload in new object[]
        {
            new { moderationStatus = "hidden", reason = (string?)null }, new { moderationStatus = (string?)null, reason = "reason" },
            new { moderationStatus = "removed", reason = "reason" }, new { moderationStatus = "hidden", reason = new string('a', 1001) }
        }) await ErrorAsync(await client.PutAsJsonAsync($"/api/v1/mod/restaurants/{world.RestaurantId}/moderation-status", payload), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        foreach (var payload in new object[] { new { status = "removed", reason = "reason" }, new { status = "hidden", reason = " " } })
            await ErrorAsync(await client.PutAsJsonAsync($"/api/v1/mod/dishes/{world.DishId}/moderation-status", payload), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        foreach (var payload in new object[]
        {
            new { outcome = "pending", resolutionAction = "none" }, new { outcome = "resolved", resolutionAction = (string?)null },
            new { outcome = "resolved", resolutionAction = "suspend_user" },
            new { outcome = "resolved", resolutionAction = "suspend_user", suspendUntil = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new { outcome = "resolved", resolutionAction = "none", resolutionNote = new string('a', 1001) }
        }) await ErrorAsync(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve", payload), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        using var malformed = new StringContent("{", System.Text.Encoding.UTF8, "application/json");
        await ErrorAsync(await client.PostAsync($"/api/v1/mod/reports/{world.ReportId}/resolve", malformed), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await factory.ExecuteDbAsync(async context =>
        {
            (await context.Reports.SingleAsync(r => r.Id == world.ReportId)).Status.Should().Be(ReportStatus.Pending);
            (await context.AuditLogs.CountAsync()).Should().Be(0);
        });
    }

    // TEST-10: Database write failure rolls back reports, restaurant visibility, notifications and audits together.
    /// <summary>Injects a test-database constraint failure to verify the actual PostgreSQL transaction boundary.</summary>
    [PostgresEndToEndFact]
    public async Task RollsBackAllEffectsWhenPersistenceFails()
    {
        var world = await SeedAsync();
        await factory.ExecuteDbAsync(context => context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE core.notifications ADD CONSTRAINT qa_reject_report_notification CHECK (type <> 'REPORT_RESOLVED')"));
        using var client = await ClientAsync(world.ModId);
        await ErrorAsync(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve",
            new { outcome = "resolved", resolutionAction = "hide_restaurant" }), HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        await factory.ExecuteDbAsync(async context =>
        {
            (await context.Restaurants.SingleAsync(r => r.Id == world.RestaurantId)).ModerationStatus.Should().Be(RestaurantModerationStatus.Visible);
            (await context.Reports.SingleAsync(r => r.Id == world.ReportId)).Status.Should().Be(ReportStatus.Pending);
            (await context.Reports.SingleAsync(r => r.Id == world.RelatedReportId)).Status.Should().Be(ReportStatus.InReview);
            (await context.AuditLogs.CountAsync()).Should().Be(0);
            (await context.Notifications.CountAsync()).Should().Be(0);
        });
    }

    // TEST-11: Concurrent conclusions on the same target produce one success and one REPORT_CLOSED without duplicate effects.
    /// <summary>Verifies row locking through two real concurrent HTTP requests and independent database contexts.</summary>
    [PostgresEndToEndFact]
    public async Task SerializesConcurrentResolutions()
    {
        var world = await SeedAsync();
        using var first = await ClientAsync(world.ModId);
        using var second = await ClientAsync(world.AdminId);
        var payload = new { outcome = "resolved", resolutionAction = "hide_restaurant" };
        var responses = await Task.WhenAll(first.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve", payload),
            second.PostAsJsonAsync($"/api/v1/mod/reports/{world.RelatedReportId}/resolve", payload));
        responses.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        await ErrorAsync(responses.Single(x => x.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "REPORT_CLOSED");
        await factory.ExecuteDbAsync(async context =>
        {
            (await context.AuditLogs.CountAsync(a => a.Action == AuditActions.RestaurantHidden)).Should().Be(1);
            (await context.AuditLogs.CountAsync(a => a.Action == AuditActions.ReportResolved)).Should().Be(2);
            (await context.Notifications.CountAsync(n => n.Type == "REPORT_RESOLVED")).Should().Be(3);
        });
        foreach (var response in responses) response.Dispose();
    }

    // TEST-12: The shared resolver supports blog actions with the matching social-report permission and content audit.
    /// <summary>Checks both blog action branches through PostgreSQL as required by the MOD-RP4 contract.</summary>
    [PostgresEndToEndFact]
    public async Task ResolvesBlogReportsWithContentActions()
    {
        foreach (var action in new[] { "hide_blog", "remove_blog" })
        {
            var world = await SeedAsync();
            using var client = await ClientAsync(world.ModId);
            var report = await DataAsync<ReportDto>(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.BlogReportId}/resolve",
                new { outcome = "resolved", resolutionAction = action }));
            report.TargetType.Should().Be("blog");
            await factory.ExecuteDbAsync(async context =>
            {
                (await context.Blogs.SingleAsync()).Status.Should().Be(action == "hide_blog" ? BlogStatus.Hidden : BlogStatus.Removed);
                (await context.AuditLogs.CountAsync(a => a.Action == (action == "hide_blog" ? AuditActions.BlogHidden : AuditActions.BlogRemoved))).Should().Be(1);
            });
        }
    }

    // TEST-13: Publish all five ANGI-191 operations in the OpenAPI document used by Scalar.
    /// <summary>Verifies endpoint discovery and the Scalar page through the running production pipeline.</summary>
    [PostgresEndToEndFact]
    public async Task PublishesAllOperationsInOpenApiAndScalar()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        paths.GetProperty("/api/v1/mod/restaurants").TryGetProperty("get", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/mod/restaurants/{restaurantId}").TryGetProperty("get", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/mod/restaurants/{restaurantId}/moderation-status").TryGetProperty("put", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/mod/dishes/{dishId}/moderation-status").TryGetProperty("put", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/mod/reports/{reportId}/resolve").TryGetProperty("post", out _).Should().BeTrue();
        using var scalar = await client.GetAsync("/scalar/v1");
        scalar.EnsureSuccessStatusCode();
        (await scalar.Content.ReadAsStringAsync()).Should().Contain("scalar");
    }

    /// <summary>Creates a dedicated test world using real migrations and deterministic core data.</summary>
    private async Task<World> SeedAsync()
    {
        await factory.ResetDatabaseAsync();
        return await factory.ExecuteDbAsync(async context =>
        {
            var owner = Account("owner@moderation.test", 2);
            var hiddenOwner = Account("hidden@moderation.test", 2);
            var deletedOwner = Account("deleted@moderation.test", 2);
            var mod = Account("mod@moderation.test", 3);
            var admin = Account("admin@moderation.test", 4);
            var traveler = Account("traveler@moderation.test", 1);
            context.Users.AddRange(owner, hiddenOwner, deletedOwner, mod, admin, traveler);
            await context.SaveChangesAsync();
            var restaurant = new Domain.Entities.Restaurant { OwnerId = owner.Id, Name = "Bún bò", Slug = "bun-bo",
                AddressLine = "1 Street", ProvinceName = "Hue", Latitude = 16.4m, Longitude = 107.5m,
                VerificationStatus = RestaurantVerificationStatus.Verified };
            for (short day = 0; day < 7; day++) restaurant.BusinessHours.Add(new RestaurantBusinessHour
            { DayOfWeek = day, OpenTime = TimeOnly.MinValue, CloseTime = new TimeOnly(23, 59, 59) });
            var hidden = new Domain.Entities.Restaurant { OwnerId = hiddenOwner.Id, Name = "Hidden", Slug = "hidden", AddressLine = "2 Street",
                ProvinceName = "Hanoi", OperatingStatus = RestaurantOperatingStatus.TemporarilyClosed, ModerationStatus = RestaurantModerationStatus.Hidden };
            var deleted = new Domain.Entities.Restaurant { OwnerId = deletedOwner.Id, Name = "Deleted", Slug = "deleted", AddressLine = "3 Street", DeletedAt = DateTime.UtcNow };
            context.Restaurants.AddRange(restaurant, hidden, deleted);
            var blog = new Blog { AuthorId = traveler.Id, Title = "Reported blog", Slug = "reported-blog", Content = "<p>Content</p>", PublishedAt = DateTime.UtcNow };
            context.Blogs.Add(blog);
            var media = new MediaFile { UploaderId = traveler.Id, StorageKey = "image/authenticated/evidence/test.jpg", Url = "https://private.test", MimeType = "image/jpeg" };
            context.MediaFiles.Add(media);
            await context.SaveChangesAsync();
            var activeDish = new Dish { RestaurantId = restaurant.Id, Name = "Active", Price = 30000 };
            var hiddenDish = new Dish { RestaurantId = restaurant.Id, Name = "Hidden", Price = 30000, Status = DishStatus.Hidden };
            var removedDish = new Dish { RestaurantId = restaurant.Id, Name = "Removed", Price = 30000, Status = DishStatus.Removed };
            context.Dishes.AddRange(activeDish, hiddenDish, removedDish);
            var report = new Report { ReporterId = traveler.Id, RestaurantId = restaurant.Id, TargetType = ReportTargetType.Restaurant,
                ReasonCode = ReportReasonCode.Hygiene, Evidence = JsonSerializer.Serialize(new[] { media.Id }) };
            var related = new Report { ReporterId = admin.Id, RestaurantId = restaurant.Id, TargetType = ReportTargetType.Restaurant,
                ReasonCode = ReportReasonCode.Closed, Status = ReportStatus.InReview, HandledBy = admin.Id };
            var blogReport = new Report { ReporterId = owner.Id, BlogId = blog.Id, TargetType = ReportTargetType.Blog, ReasonCode = ReportReasonCode.Spam };
            context.Reports.AddRange(report, related, blogReport);
            context.UserSessions.Add(new UserSession { UserId = owner.Id, RefreshTokenHash = "owner-session-test-hash", ExpiresAt = DateTime.UtcNow.AddDays(30) });
            await context.SaveChangesAsync();
            return new World(restaurant.Id, hidden.Id, deleted.Id, activeDish.Id, removedDish.Id, report.Id, related.Id, blogReport.Id,
                owner.Id, mod.Id, admin.Id, traveler.Id, media.Id);
        });
    }

    /// <summary>Builds an active verified account for a deterministic test role.</summary>
    private static User Account(string email, short role) => new()
    {
        Email = email, DisplayName = email, RoleId = role, Status = UserStatus.Active, EmailVerifiedAt = DateTime.UtcNow
    };

    /// <summary>Creates an HTTP client with a real signed access token for the seeded database user.</summary>
    private async Task<HttpClient> ClientAsync(int userId)
    {
        var user = await factory.ExecuteDbAsync(context => context.Users.AsNoTracking().Include(u => u.Role).SingleAsync(u => u.Id == userId));
        using var scope = factory.Services.CreateScope();
        var token = scope.ServiceProvider.GetRequiredService<IAuthenticationTokenService>().CreateAccessToken(user, 1);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    /// <summary>Denies one default permission for the selected Mod using a real user override.</summary>
    private Task DenyAsync(int userId, string code) => factory.ExecuteDbAsync(async context =>
    {
        var permission = await context.Permissions.SingleAsync(p => p.Code == code);
        context.UserPermissions.Add(new UserPermission { UserId = userId, PermissionId = permission.Id, Effect = PermissionEffect.Deny,
            GrantedBy = userId });
        await context.SaveChangesAsync();
    });

    /// <summary>Retrieves a successful moderator list page and checks the standard response envelope.</summary>
    private static async Task<Paged<ModRestaurantCardDto>> PageAsync(HttpClient client, string query) =>
        await DataAsync<Paged<ModRestaurantCardDto>>(await client.GetAsync("/api/v1/mod/restaurants" + query));

    /// <summary>Checks HTTP 200 and deserializes the documented successful response data.</summary>
    private static async Task<T> DataAsync<T>(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }

    /// <summary>Checks the HTTP status, error code, null data and validation field errors of a failed response.</summary>
    private static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<JsonElement?>>();
        envelope!.Success.Should().BeFalse();
        envelope.ErrorCode.Should().Be(code);
        if (code == "ACCOUNT_SUSPENDED")
            envelope.Data!.Value.GetProperty("suspendedUntil").ValueKind.Should().Be(JsonValueKind.String);
        else envelope.Data.Should().BeNull();
        if (code == "VALIDATION_FAILED") envelope.Errors.Should().NotBeNullOrEmpty();
    }
}
