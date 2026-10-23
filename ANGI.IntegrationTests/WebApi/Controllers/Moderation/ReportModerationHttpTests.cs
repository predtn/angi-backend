using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.IntegrationTests.WebApi.Controllers.Moderation;

/// <summary>Checks the complete ANGI-194 report workflow against the real HTTP pipeline and PostgreSQL.</summary>
public sealed class ReportModerationHttpTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private sealed record World(long RestaurantId, long OtherRestaurantId, long BlogId, long ReportId,
        long RelatedId, long ClosedId, long BlogReportId, int ModId, int OtherModId, int AdminId, int TravelerId, int OwnerId);

    // TEST-01: Apply every queue filter, count all open reports on each target and paginate deterministically.
    /// <summary>Checks target isolation, status/reason partitions, combined filters, counts and offset overflow.</summary>
    [PostgresEndToEndFact]
    public async Task ListsAndFiltersReportQueue()
    {
        var world = await SeedAsync();
        using var client = await ClientAsync(world.ModId);
        var all = await PageAsync(client, "targetType=restaurant");
        all.TotalItems.Should().Be(7);
        all.Items.Should().OnlyContain(report => report.TargetType == "restaurant");
        all.Items.Where(report => report.TargetId == world.RestaurantId).Should().OnlyContain(report => report.ReportCountOnTarget == 2);
        all.Items.Select(report => report.Id).Should().BeInDescendingOrder();
        var blogs = await PageAsync(client, "targetType=blog");
        blogs.Items.Should().ContainSingle(report => report.Id == world.BlogReportId && report.ReportCountOnTarget == 1);
        foreach (var status in new[] { "pending", "in_review", "resolved", "dismissed" })
        {
            var page = await PageAsync(client, $"targetType=restaurant&status={status}");
            page.Items.Should().NotBeEmpty().And.OnlyContain(report => report.Status == status);
        }
        foreach (var reason in new[] { "wrong_info", "closed", "spam", "offensive", "hygiene", "scam", "other" })
        {
            var page = await PageAsync(client, $"targetType=restaurant&reasonCode={reason}");
            page.Items.Should().NotBeEmpty().And.OnlyContain(report => report.ReasonCode == reason);
        }
        var selected = await PageAsync(client, $"targetType=restaurant&status=pending&reasonCode=hygiene&targetId={world.RestaurantId}");
        selected.Items.Should().ContainSingle(report => report.Id == world.ReportId && report.ReportCountOnTarget == 2);
        var ids = new List<long>();
        for (var page = 1; page <= 7; page++)
            ids.Add((await PageAsync(client, $"targetType=restaurant&page={page}&pageSize=1")).Items.Single().Id);
        ids.Should().Equal(all.Items.Select(report => report.Id));
        (await PageAsync(client, "targetType=restaurant&page=8&pageSize=1")).Items.Should().BeEmpty();
        (await PageAsync(client, "targetType=restaurant&page=2147483647&pageSize=100")).Items.Should().BeEmpty();
        var empty = await PageAsync(client, "targetType=restaurant&targetId=9223372036854775807");
        empty.TotalItems.Should().Be(0);
        empty.TotalPages.Should().Be(0);
        empty.Items.Should().BeEmpty();
    }

    // TEST-02: Return restaurant and blog targets with signed evidence, public identities and only other open reports.
    /// <summary>Checks detail JSON, closed-report history, target contracts and absence of private storage/account data.</summary>
    [PostgresEndToEndFact]
    public async Task ReturnsAuthorizedReportDetail()
    {
        var world = await SeedAsync();
        using var client = await ClientAsync(world.ModId);
        using var response = await client.GetAsync($"/api/v1/mod/reports/{world.ReportId}");
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("id").GetInt64().Should().Be(world.ReportId);
        data.GetProperty("target").GetProperty("id").GetInt64().Should().Be(world.RestaurantId);
        data.GetProperty("target").GetProperty("name").GetString().Should().Be("Hidden restaurant");
        data.GetProperty("target").TryGetProperty("owner", out _).Should().BeFalse();
        data.GetProperty("target").TryGetProperty("moderationStatus", out _).Should().BeFalse();
        data.GetProperty("evidenceUrls").EnumerateArray().Should().ContainSingle(url => url.GetString()!.StartsWith("https://media.integration/signed/"));
        data.GetProperty("reporter").GetProperty("id").GetInt32().Should().Be(world.TravelerId);
        data.GetProperty("relatedReports").EnumerateArray().Should().ContainSingle(report => report.GetProperty("id").GetInt64() == world.RelatedId);
        data.GetProperty("reportCountOnTarget").GetInt32().Should().Be(2);
        body.Should().NotContain("passwordHash").And.NotContain("refreshTokenHash").And.NotContain("storageKey").And.NotContain("@reports.test");
        var closed = await DataAsync<JsonElement>(await client.GetAsync($"/api/v1/mod/reports/{world.ClosedId}"));
        closed.GetProperty("relatedReports").GetArrayLength().Should().Be(2);
        var blog = await DataAsync<JsonElement>(await client.GetAsync($"/api/v1/mod/reports/{world.BlogReportId}"));
        var target = blog.GetProperty("target");
        target.GetProperty("title").GetString().Should().Be("Hidden blog");
        target.GetProperty("content").GetString().Should().Be("<p>Reported content</p>");
        target.GetProperty("images").GetArrayLength().Should().Be(1);
        target.GetProperty("likedByMe").GetBoolean().Should().BeTrue();
        target.GetProperty("isMine").GetBoolean().Should().BeFalse();
        target.GetProperty("viewCount").GetInt32().Should().Be(5);
        await factory.ExecuteDbAsync(async context => (await context.Blogs.SingleAsync(item => item.Id == world.BlogId)).ViewCount.Should().Be(5));
    }

    // TEST-03: Persist a claim once and reject every later claim or terminal report state.
    /// <summary>Checks claim fields, no conclusion side effects, both target types and end-to-end resolution after claiming.</summary>
    [PostgresEndToEndFact]
    public async Task ClaimsThenResolvesReport()
    {
        var world = await SeedAsync();
        using var client = await ClientAsync(world.ModId);
        var report = await DataAsync<ReportDto>(await client.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null));
        report.Status.Should().Be("in_review");
        report.HandledBy!.Id.Should().Be(world.ModId);
        report.HandledAt.Should().BeNull();
        report.ResolutionAction.Should().BeNull();
        report.ReportCountOnTarget.Should().Be(2);
        await ErrorAsync(await client.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null), HttpStatusCode.Conflict, "REPORT_ALREADY_CLAIMED");
        using var other = await ClientAsync(world.OtherModId);
        await ErrorAsync(await other.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null), HttpStatusCode.Conflict, "REPORT_ALREADY_CLAIMED");
        await factory.ExecuteDbAsync(async context =>
        {
            var saved = await context.Reports.SingleAsync(item => item.Id == world.ReportId);
            saved.Status.Should().Be(ReportStatus.InReview);
            saved.HandledBy.Should().Be(world.ModId);
            (await context.AuditLogs.CountAsync()).Should().Be(0);
            (await context.Notifications.CountAsync()).Should().Be(0);
        });
        await DataAsync<ReportDto>(await client.PostAsync($"/api/v1/mod/reports/{world.BlogReportId}/claim", null));
        report = await DataAsync<ReportDto>(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve",
            new { outcome = "resolved", resolutionAction = "suspend_restaurant", resolutionNote = "Confirmed" }));
        report.Status.Should().Be("resolved");
        report.HandledAt.Should().NotBeNull();
        report.ReportCountOnTarget.Should().Be(0);
        await ErrorAsync(await client.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null), HttpStatusCode.Conflict, "REPORT_CLOSED");
        var dismissed = await factory.ExecuteDbAsync(context => context.Reports.Where(item => item.Status == ReportStatus.Dismissed).Select(item => item.Id).FirstAsync());
        await ErrorAsync(await client.PostAsync($"/api/v1/mod/reports/{dismissed}/claim", null), HttpStatusCode.Conflict, "REPORT_CLOSED");
    }

    // TEST-04: Only one of two simultaneous moderators can claim a pending report.
    /// <summary>Checks the real PostgreSQL row-lock behavior and matches the committed handler to the winning response.</summary>
    [PostgresEndToEndFact]
    public async Task SerializesConcurrentClaims()
    {
        var world = await SeedAsync();
        using var first = await ClientAsync(world.ModId);
        using var second = await ClientAsync(world.OtherModId);
        var responses = await Task.WhenAll(first.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null),
            second.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null));
        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        var winner = await DataAsync<ReportDto>(responses.Single(response => response.IsSuccessStatusCode));
        await ErrorAsync(responses.Single(response => !response.IsSuccessStatusCode), HttpStatusCode.Conflict, "REPORT_ALREADY_CLAIMED");
        await factory.ExecuteDbAsync(async context =>
            (await context.Reports.SingleAsync(report => report.Id == world.ReportId)).HandledBy.Should().Be(winner.HandledBy!.Id));
    }

    // TEST-05: A concurrent claim cannot overwrite a report conclusion or reopen a terminal state.
    /// <summary>Checks shared claim/resolve locking and absence of deadlocks or lost updates.</summary>
    [PostgresEndToEndFact]
    public async Task SerializesClaimWithResolution()
    {
        var world = await SeedAsync();
        using var first = await ClientAsync(world.ModId);
        using var second = await ClientAsync(world.OtherModId);
        var responses = await Task.WhenAll(first.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null),
            second.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve", new { outcome = "resolved", resolutionAction = "none" }));
        responses[1].StatusCode.Should().Be(HttpStatusCode.OK);
        responses[0].StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
        if (responses[0].StatusCode == HttpStatusCode.Conflict) await ErrorAsync(responses[0], HttpStatusCode.Conflict, "REPORT_CLOSED");
        await factory.ExecuteDbAsync(async context =>
        {
            var report = await context.Reports.SingleAsync(item => item.Id == world.ReportId);
            report.Status.Should().Be(ReportStatus.Resolved);
            report.HandledBy.Should().Be(world.OtherModId);
            (await context.AuditLogs.CountAsync()).Should().Be(2);
        });
    }

    // TEST-06: Reject anonymous, wrong-role, wrong-target and explicitly denied permission callers on every route.
    /// <summary>Checks real JWT, middleware, role policies, database deny overrides and Admin access.</summary>
    [PostgresEndToEndFact]
    public async Task EnforcesTargetSpecificPermissions()
    {
        var world = await SeedAsync();
        using var guest = factory.CreateClient();
        using var traveler = await ClientAsync(world.TravelerId);
        using var owner = await ClientAsync(world.OwnerId);
        foreach (var client in new[] { guest, traveler, owner })
        {
            var status = client == guest ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            var code = client == guest ? "UNAUTHORIZED" : "FORBIDDEN";
            await ErrorAsync(await client.GetAsync("/api/v1/mod/reports?targetType=restaurant"), status, code);
            await ErrorAsync(await client.GetAsync($"/api/v1/mod/reports/{world.ReportId}"), status, code);
            await ErrorAsync(await client.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null), status, code);
            await ErrorAsync(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve", new { outcome = "resolved", resolutionAction = "none" }), status, code);
        }
        await DenyAsync(world.ModId, "REPORT_RESTAURANT_HANDLE");
        using var mod = await ClientAsync(world.ModId);
        await ErrorAsync(await mod.GetAsync("/api/v1/mod/reports?targetType=restaurant"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ErrorAsync(await mod.GetAsync($"/api/v1/mod/reports/{world.ReportId}"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ErrorAsync(await mod.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null), HttpStatusCode.Forbidden, "FORBIDDEN");
        (await mod.GetAsync("/api/v1/mod/reports?targetType=blog")).EnsureSuccessStatusCode();
        (await mod.GetAsync($"/api/v1/mod/reports/{world.BlogReportId}")).EnsureSuccessStatusCode();
        await DenyAsync(world.OtherModId, "REPORT_SOCIAL_HANDLE");
        using var other = await ClientAsync(world.OtherModId);
        await ErrorAsync(await other.GetAsync("/api/v1/mod/reports?targetType=blog"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ErrorAsync(await other.GetAsync($"/api/v1/mod/reports/{world.BlogReportId}"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ErrorAsync(await other.PostAsync($"/api/v1/mod/reports/{world.BlogReportId}/claim", null), HttpStatusCode.Forbidden, "FORBIDDEN");
        using var admin = await ClientAsync(world.AdminId);
        (await admin.GetAsync("/api/v1/mod/reports?targetType=restaurant")).EnsureSuccessStatusCode();
        (await admin.GetAsync($"/api/v1/mod/reports/{world.ReportId}")).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null)).EnsureSuccessStatusCode();
    }

    // TEST-07: Reject invalid query enums, numbers, missing target types and out-of-range identifiers with standard errors.
    /// <summary>Checks actual model binding, validation field names and nonexistent route identifiers.</summary>
    [PostgresEndToEndFact]
    public async Task RejectsInvalidQueriesAndMissingReports()
    {
        var world = await SeedAsync();
        using var client = await ClientAsync(world.ModId);
        foreach (var query in new[] { "", "targetType=0", "targetType=Restaurant", "targetType=review", "targetType=restaurant&page=0",
            "targetType=restaurant&page=-1", "targetType=restaurant&pageSize=0", "targetType=restaurant&pageSize=101",
            "targetType=restaurant&targetId=0", "targetType=restaurant&targetId=-1", "targetType=restaurant&targetId=invalid",
            "targetType=restaurant&targetId=9223372036854775808", "targetType=restaurant&status=PENDING",
            "targetType=restaurant&reasonCode=invalid", "targetType=restaurant&page=2147483648", "targetType=restaurant&pageSize=invalid" })
        {
            await ErrorAsync(await client.GetAsync("/api/v1/mod/reports?" + query), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        }
        foreach (var id in new[] { -1L, 0L, long.MaxValue })
        {
            await ErrorAsync(await client.GetAsync($"/api/v1/mod/reports/{id}"), HttpStatusCode.NotFound, "REPORT_NOT_FOUND");
            await ErrorAsync(await client.PostAsync($"/api/v1/mod/reports/{id}/claim", null), HttpStatusCode.NotFound, "REPORT_NOT_FOUND");
        }
    }

    // TEST-08: Database failure while claiming rolls back both the status and moderator assignment.
    /// <summary>Injects a dedicated-test-database constraint failure and verifies a subsequent valid retry succeeds.</summary>
    [PostgresEndToEndFact]
    public async Task RollsBackFailedClaim()
    {
        var world = await SeedAsync();
        await factory.ExecuteDbAsync(context => context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE core.reports ADD CONSTRAINT qa_reject_claim CHECK (status <> 'in_review') NOT VALID"));
        using var client = await ClientAsync(world.ModId);
        try
        {
            await ErrorAsync(await client.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null), HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
            await factory.ExecuteDbAsync(async context =>
            {
                var report = await context.Reports.SingleAsync(item => item.Id == world.ReportId);
                report.Status.Should().Be(ReportStatus.Pending);
                report.HandledBy.Should().BeNull();
            });
        }
        finally
        {
            await factory.ExecuteDbAsync(context => context.Database.ExecuteSqlRawAsync("ALTER TABLE core.reports DROP CONSTRAINT qa_reject_claim"));
        }
        (await client.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null)).EnsureSuccessStatusCode();
    }

    // TEST-09: Expose all four report operations through the OpenAPI document used by Scalar.
    /// <summary>Checks route discovery and supported HTTP methods for the complete task.</summary>
    [PostgresEndToEndFact]
    public async Task PublishesReportOperations()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        paths.GetProperty("/api/v1/mod/reports").TryGetProperty("get", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/mod/reports/{reportId}").TryGetProperty("get", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/mod/reports/{reportId}/claim").TryGetProperty("post", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/mod/reports/{reportId}/resolve").TryGetProperty("post", out _).Should().BeTrue();
    }

    // TEST-10: A valid staff JWT cannot bypass inactive account checks on any report operation.
    /// <summary>Checks pending-verification, suspended, banned and deactivated account partitions without report writes.</summary>
    [PostgresEndToEndFact]
    public async Task BlocksInactiveModeratorAccounts()
    {
        var world = await SeedAsync();
        using var client = await ClientAsync(world.ModId);
        foreach (var (status, code) in new[]
        {
            (UserStatus.PendingVerification, "EMAIL_NOT_VERIFIED"), (UserStatus.Suspended, "ACCOUNT_SUSPENDED"),
            (UserStatus.Banned, "ACCOUNT_BANNED"), (UserStatus.Deactivated, "ACCOUNT_DEACTIVATED")
        })
        {
            await factory.ExecuteDbAsync(async context =>
            {
                var user = await context.Users.SingleAsync(item => item.Id == world.ModId);
                user.Status = status;
                user.SuspendedUntil = status == UserStatus.Suspended ? DateTime.UtcNow.AddDays(1) : null;
                await context.SaveChangesAsync();
            });
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IAccountStatusCache>().Invalidate(world.ModId);
            await ErrorAsync(await client.GetAsync("/api/v1/mod/reports?targetType=restaurant"), HttpStatusCode.Forbidden, code);
            await ErrorAsync(await client.GetAsync($"/api/v1/mod/reports/{world.ReportId}"), HttpStatusCode.Forbidden, code);
            await ErrorAsync(await client.PostAsync($"/api/v1/mod/reports/{world.ReportId}/claim", null), HttpStatusCode.Forbidden, code);
            await ErrorAsync(await client.PostAsJsonAsync($"/api/v1/mod/reports/{world.ReportId}/resolve",
                new { outcome = "resolved", resolutionAction = "none" }), HttpStatusCode.Forbidden, code);
        }
        await factory.ExecuteDbAsync(async context =>
        {
            (await context.Reports.SingleAsync(item => item.Id == world.ReportId)).Status.Should().Be(ReportStatus.Pending);
            (await context.AuditLogs.CountAsync()).Should().Be(0);
        });
    }

    /// <summary>Seeds overlapping restaurant/blog identifiers, all report states, private evidence and multiple moderators.</summary>
    private async Task<World> SeedAsync()
    {
        await factory.ResetDatabaseAsync();
        return await factory.ExecuteDbAsync(async context =>
        {
            var owner = Account("owner", 2);
            var otherOwner = Account("other-owner", 2);
            var mod = Account("mod", 3);
            var otherMod = Account("other-mod", 3);
            var admin = Account("admin", 4);
            var traveler = Account("traveler", 1);
            context.Users.AddRange(owner, otherOwner, mod, otherMod, admin, traveler);
            await context.SaveChangesAsync();
            var restaurant = new Domain.Entities.Restaurant { OwnerId = owner.Id, Name = "Hidden restaurant", Slug = "hidden-restaurant", AddressLine = "1 Street",
                ModerationStatus = RestaurantModerationStatus.Hidden, Latitude = 16.4m, Longitude = 107.5m };
            var other = new Domain.Entities.Restaurant { OwnerId = otherOwner.Id, Name = "Other", Slug = "other", AddressLine = "2 Street" };
            var blog = new Blog { AuthorId = traveler.Id, Title = "Hidden blog", Slug = "hidden-blog", Content = "<p>Reported content</p>",
                Status = BlogStatus.Hidden, ViewCount = 5, PublishedAt = DateTime.UtcNow };
            var media = new MediaFile { UploaderId = traveler.Id, StorageKey = "image/authenticated/evidence/private.jpg", Url = "https://private.test/secret", MimeType = "image/jpeg" };
            var image = new MediaFile { UploaderId = traveler.Id, StorageKey = "image/upload/blog/cover.jpg", Url = "https://public.test/blog.jpg", MimeType = "image/jpeg" };
            context.Restaurants.AddRange(restaurant, other);
            context.Blogs.Add(blog);
            context.MediaFiles.AddRange(media, image);
            await context.SaveChangesAsync();
            blog.Images.Add(new BlogImage { MediaId = image.Id });
            context.BlogLikes.Add(new BlogLike { BlogId = blog.Id, UserId = mod.Id });
            var report = new Report { ReporterId = traveler.Id, TargetType = ReportTargetType.Restaurant, RestaurantId = restaurant.Id,
                ReasonCode = ReportReasonCode.Hygiene, Evidence = JsonSerializer.Serialize(new[] { media.Id }) };
            var related = new Report { ReporterId = admin.Id, TargetType = ReportTargetType.Restaurant, RestaurantId = restaurant.Id,
                ReasonCode = ReportReasonCode.Closed, Status = ReportStatus.InReview, HandledBy = otherMod.Id };
            var closed = new Report { ReporterId = traveler.Id, TargetType = ReportTargetType.Restaurant, RestaurantId = restaurant.Id,
                ReasonCode = ReportReasonCode.Scam, Status = ReportStatus.Resolved, HandledBy = otherMod.Id, ResolutionAction = ResolutionAction.None };
            var blogReport = new Report { ReporterId = owner.Id, TargetType = ReportTargetType.Blog, BlogId = blog.Id, ReasonCode = ReportReasonCode.Spam };
            context.Reports.AddRange(report, related, closed, blogReport,
                new Report { ReporterId = traveler.Id, TargetType = ReportTargetType.Restaurant, RestaurantId = other.Id, ReasonCode = ReportReasonCode.WrongInfo },
                new Report { ReporterId = traveler.Id, TargetType = ReportTargetType.Restaurant, RestaurantId = other.Id, ReasonCode = ReportReasonCode.Spam, Status = ReportStatus.Dismissed },
                new Report { ReporterId = traveler.Id, TargetType = ReportTargetType.Restaurant, RestaurantId = other.Id, ReasonCode = ReportReasonCode.Offensive, Status = ReportStatus.Resolved },
                new Report { ReporterId = admin.Id, TargetType = ReportTargetType.Restaurant, RestaurantId = other.Id, ReasonCode = ReportReasonCode.Other, Status = ReportStatus.Dismissed });
            await context.SaveChangesAsync();
            return new World(restaurant.Id, other.Id, blog.Id, report.Id, related.Id, closed.Id, blogReport.Id,
                mod.Id, otherMod.Id, admin.Id, traveler.Id, owner.Id);
        });
    }

    /// <summary>Creates a verified, active account under a seeded role.</summary>
    private static User Account(string name, short role) => new()
    { Email = name + "@reports.test", DisplayName = name, RoleId = role, Status = UserStatus.Active, EmailVerifiedAt = DateTime.UtcNow };

    /// <summary>Creates a client carrying a real JWT for a seeded database identity.</summary>
    private async Task<HttpClient> ClientAsync(int userId)
    {
        var user = await factory.ExecuteDbAsync(context => context.Users.AsNoTracking().Include(item => item.Role).SingleAsync(item => item.Id == userId));
        using var scope = factory.Services.CreateScope();
        var token = scope.ServiceProvider.GetRequiredService<IAuthenticationTokenService>().CreateAccessToken(user, 1);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    /// <summary>Adds a database deny override for a single effective moderator permission.</summary>
    private Task DenyAsync(int userId, string code) => factory.ExecuteDbAsync(async context =>
    {
        var permission = await context.Permissions.SingleAsync(item => item.Code == code);
        context.UserPermissions.Add(new UserPermission { UserId = userId, PermissionId = permission.Id, Effect = PermissionEffect.Deny, GrantedBy = userId });
        await context.SaveChangesAsync();
    });

    /// <summary>Reads a report queue page through the real HTTP route.</summary>
    private static async Task<Paged<ReportDto>> PageAsync(HttpClient client, string query) =>
        await DataAsync<Paged<ReportDto>>(await client.GetAsync("/api/v1/mod/reports?" + query));

    /// <summary>Checks the successful standard envelope and deserializes its data.</summary>
    private static async Task<T> DataAsync<T>(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        envelope!.Success.Should().BeTrue();
        envelope.ErrorCode.Should().BeNull();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }

    /// <summary>Checks the standard error status, code, null data and validation details.</summary>
    private static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<JsonElement?>>();
        envelope!.Success.Should().BeFalse();
        envelope.ErrorCode.Should().Be(code);
        if (code == "ACCOUNT_SUSPENDED") envelope.Data!.Value.GetProperty("suspendedUntil").ValueKind.Should().Be(JsonValueKind.String);
        else envelope.Data.Should().BeNull();
        if (code == "VALIDATION_FAILED") envelope.Errors.Should().NotBeNullOrEmpty();
    }
}
