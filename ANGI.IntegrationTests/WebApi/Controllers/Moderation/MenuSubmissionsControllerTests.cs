using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.IntegrationTests.WebApi.Controllers.Moderation;

/// <summary>Exercises MOD-M1 through MOD-M4 through the real HTTP pipeline, repositories, transactions and PostgreSQL triggers.</summary>
public sealed class MenuSubmissionsControllerTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private const string Root = "/api/v1/mod/menu-submissions";

    // TEST-01: Approve a full menu replacement and preserve edited dish identities, counters and availability.
    /// <summary>Verifies result identifiers, tag replacement, owner notification, audit actor and exact trigger-generated outbox events.</summary>
    [PostgresEndToEndFact]
    public async Task Approve_ShouldPersistAtomicMenuReplacement()
    {
        var seed = await SeedAsync();
        using var client = await ClientAsync(seed.Mod);
        var baseline = await OutboxCountAsync();
        using var response = await client.PostAsJsonAsync($"{Root}/{seed.Submission}/approve", new { reviewNote = "Approved menu" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await DataAsync<ApproveMenuSubmissionResponseDto>(response);
        result.Status.Should().Be("approved");
        result.ReviewedBy!.Id.Should().Be(seed.Mod);
        result.ReviewedAt.Should().NotBeNull();
        result.CreatedDishIds.Should().ContainSingle();
        result.UpdatedDishIds.Should().BeEquivalentTo(new[] { seed.Edited, seed.PriceOnly });
        result.RemovedDishIds.Should().BeEquivalentTo(new[] { seed.Removed, seed.HiddenRemoved });
        await factory.ExecuteDbAsync(async db =>
        {
            var dishes = await db.Dishes.Include(x => x.DishTags).Where(x => x.RestaurantId == seed.Restaurant).ToListAsync();
            var edited = dishes.Single(x => x.Id == seed.Edited);
            edited.Name.Should().Be("Edited dish");
            edited.IsAvailable.Should().BeFalse();
            edited.LikeCount.Should().Be(12);
            edited.DislikeCount.Should().Be(3);
            edited.DishTags.Select(x => x.TagId).Should().BeEquivalentTo(new short[] { 2, 3 });
            dishes.Single(x => x.Id == seed.Unchanged).Status.Should().Be(DishStatus.Hidden);
            dishes.Single(x => x.Id == seed.Removed).Status.Should().Be(DishStatus.Removed);
            dishes.Single(x => x.Id == seed.HiddenRemoved).Status.Should().Be(DishStatus.Removed);
            var added = dishes.Single(x => x.Id == result.CreatedDishIds.Single());
            added.Status.Should().Be(DishStatus.Active);
            added.IsAvailable.Should().BeTrue();
            added.DishTags.Select(x => x.TagId).Should().Equal((short)1);
            var items = await db.MenuSubmissionItems.Where(x => x.SubmissionId == seed.Submission).ToListAsync();
            items.Should().OnlyContain(x => x.ResultDishId.HasValue);
            items.Single(x => x.SourceDishId == seed.Edited).ResultDishId.Should().Be(seed.Edited);
            items.Single(x => x.SourceDishId == null).ResultDishId.Should().Be(added.Id);
            var audit = await db.AuditLogs.SingleAsync(x => x.Action == "MENU_APPROVED");
            audit.EntityId.Should().Be(seed.Submission);
            audit.ActorId.Should().Be(seed.Mod);
            audit.ActorRole.Should().Be("MOD");
            audit.SubjectUserId.Should().Be(seed.Owner);
            using var values = JsonDocument.Parse(audit.NewValues!);
            values.RootElement.GetProperty("reason").GetString().Should().Be("Approved menu");
            var notification = await db.Notifications.SingleAsync(x => x.Type == "MENU_APPROVED");
            notification.RecipientId.Should().Be(seed.Owner);
            notification.Body.Should().Be("Approved menu");
            using var data = JsonDocument.Parse(notification.Data);
            data.RootElement.GetProperty("submission_id").GetInt64().Should().Be(seed.Submission);
            var events = await db.OutboxMessages.OrderBy(x => x.Id).Skip(baseline).ToListAsync();
            events.Select(x => (x.EventType, x.AggregateKey)).Should().BeEquivalentTo(new[]
            {
                (OutboxEventType.DishUpserted, $"dish:{seed.Edited}"),
                (OutboxEventType.DishUpserted, $"dish:{added.Id}"),
                (OutboxEventType.DishDeactivated, $"dish:{seed.Removed}")
            });
        });
        using var repeated = await client.PostAsJsonAsync($"{Root}/{seed.Submission}/approve", new { });
        await ErrorAsync(repeated, HttpStatusCode.Conflict, "SUBMISSION_NOT_PENDING");
        (await OutboxCountAsync()).Should().Be(baseline + 3);
    }

    // TEST-02: Reject a pending menu with a required reason while preserving the entire current menu.
    /// <summary>Verifies rejection metadata, notification, audit and absence of dish/outbox mutations.</summary>
    [PostgresEndToEndFact]
    public async Task Reject_ShouldPreserveMenu()
    {
        var seed = await SeedAsync();
        using var client = await ClientAsync(seed.Mod);
        var before = await MenuSnapshotAsync(seed.Restaurant);
        var baseline = await OutboxCountAsync();
        using var response = await client.PostAsJsonAsync($"{Root}/{seed.Submission}/reject", new { reviewNote = "Please fix descriptions" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await DataAsync<MenuSubmissionDto>(response);
        result.Status.Should().Be("rejected");
        result.ReviewNote.Should().Be("Please fix descriptions");
        result.ReviewedBy!.Id.Should().Be(seed.Mod);
        (await MenuSnapshotAsync(seed.Restaurant)).Should().Be(before);
        (await OutboxCountAsync()).Should().Be(baseline);
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.MenuSubmissionItems.Where(x => x.SubmissionId == seed.Submission).ToListAsync())
                .Should().OnlyContain(x => x.ResultDishId == null);
            (await db.Notifications.SingleAsync()).Type.Should().Be("MENU_REJECTED");
            (await db.Notifications.SingleAsync()).RecipientId.Should().Be(seed.Owner);
            (await db.AuditLogs.SingleAsync(x => x.Action == "MENU_REJECTED")).EntityId.Should().Be(seed.Submission);
        });
    }

    // TEST-03: Return flattened comparison data with new, edited, unchanged and removed classifications.
    /// <summary>Verifies before values, tag and cover URLs, ordering and absence of nested submission wrappers.</summary>
    [PostgresEndToEndFact]
    public async Task Detail_ShouldReturnDocumentedDiff()
    {
        var seed = await SeedAsync();
        using var client = await ClientAsync(seed.Mod);
        using var response = await client.GetAsync($"{Root}/{seed.Submission}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await DataAsync<ModMenuSubmissionDetailDto>(response);
        detail.Restaurant.Id.Should().Be(seed.Restaurant);
        detail.Items.Count(x => x.Change == "new").Should().Be(1);
        detail.Items.Count(x => x.Change == "edited").Should().Be(2);
        detail.Items.Count(x => x.Change == "unchanged").Should().Be(1);
        detail.Items.Single(x => x.SourceDishId == seed.Edited).Before!.Name.Should().Be("Original dish");
        detail.Items.Single(x => x.SourceDishId == seed.Edited).Before!.CoverUrl.Should().Be("https://cdn.test/old.jpg");
        detail.Items.Single(x => x.SourceDishId == seed.Edited).CoverUrl.Should().Be("https://cdn.test/new.jpg");
        detail.Items.Where(x => x.Change != "edited").Should().OnlyContain(x => x.Before == null);
        detail.RemovedDishes.Select(x => x.Id).Should().BeEquivalentTo(new[] { seed.Removed, seed.HiddenRemoved });
        detail.Items.Select(x => x.SortOrder).Should().BeInAscendingOrder();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("data").TryGetProperty("submission", out _).Should().BeFalse();
    }

    // TEST-04: Filter the queue by status and restaurant, paginate stably and omit draft/cancelled or deleted restaurants.
    /// <summary>Verifies defaults, all allowed statuses, total counts and empty pages against SQL.</summary>
    [PostgresEndToEndFact]
    public async Task List_ShouldFilterAndPaginate()
    {
        var seed = await SeedAsync();
        using var client = await ClientAsync(seed.Mod);
        await factory.ExecuteDbAsync(async db =>
        {
            foreach (var status in new[] { MenuSubmissionStatus.Approved, MenuSubmissionStatus.Rejected, MenuSubmissionStatus.Cancelled })
                db.MenuSubmissions.Add(new MenuSubmission { RestaurantId = seed.Restaurant, CreatedBy = seed.Owner, Status = status });
            await db.SaveChangesAsync();
        });
        using var response = await client.GetAsync(Root);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await DataAsync<ANGI.Application.Common.Models.Paged<ModMenuSubmissionCardDto>>(response);
        page.TotalItems.Should().Be(1);
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
        page.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { NewCount = 1, EditedCount = 2, RemovedCount = 2 });
        foreach (var status in new[] { "approved", "rejected" })
        {
            using var filtered = await client.GetAsync($"{Root}?status={status}&restaurantId={seed.Restaurant}&pageSize=1");
            var selected = await DataAsync<ANGI.Application.Common.Models.Paged<ModMenuSubmissionCardDto>>(filtered);
            selected.TotalItems.Should().Be(1);
            selected.Items.Should().ContainSingle(x => x.Status == status);
        }
        using var missingRestaurant = await client.GetAsync($"{Root}?restaurantId=999999");
        (await DataAsync<ANGI.Application.Common.Models.Paged<ModMenuSubmissionCardDto>>(missingRestaurant)).TotalItems.Should().Be(0);
        using var beyond = await client.GetAsync($"{Root}?page=2&pageSize=1");
        var empty = await DataAsync<ANGI.Application.Common.Models.Paged<ModMenuSubmissionCardDto>>(beyond);
        empty.Items.Should().BeEmpty();
        empty.TotalPages.Should().Be(1);
        await factory.ExecuteDbAsync(async db =>
            await db.Restaurants.Where(x => x.Id == seed.Restaurant).ExecuteUpdateAsync(x => x.SetProperty(r => r.DeletedAt, DateTime.UtcNow)));
        using var deleted = await client.GetAsync(Root);
        (await DataAsync<ANGI.Application.Common.Models.Paged<ModMenuSubmissionCardDto>>(deleted)).TotalItems.Should().Be(0);
        using var detail = await client.GetAsync($"{Root}/{seed.Submission}");
        await ErrorAsync(detail, HttpStatusCode.NotFound, "SUBMISSION_NOT_FOUND");
        using var approve = await client.PostAsJsonAsync($"{Root}/{seed.Submission}/approve", new { });
        await ErrorAsync(approve, HttpStatusCode.NotFound, "SUBMISSION_NOT_FOUND");
    }

    // TEST-05: Evaluate role defaults, allow and deny overrides, ADMIN bypass and stale JWT roles using current database state.
    /// <summary>Verifies permission changes take effect immediately and every menu operation fails closed.</summary>
    [PostgresEndToEndFact]
    public async Task Permissions_ShouldUseDatabaseState()
    {
        var seed = await SeedAsync();
        using var mod = await ClientAsync(seed.Mod);
        await factory.ExecuteDbAsync(async db =>
        {
            var permission = await db.Permissions.SingleAsync(x => x.Code == "MENU_APPROVE");
            db.UserPermissions.Add(new UserPermission { UserId = seed.Mod, PermissionId = permission.Id, Effect = PermissionEffect.Deny });
            await db.SaveChangesAsync();
        });
        foreach (var path in new[] { Root, $"{Root}/{seed.Submission}" })
            await ErrorAsync(await mod.GetAsync(path), HttpStatusCode.Forbidden, "FORBIDDEN");
        foreach (var decision in new[] { "approve", "reject" })
            await ErrorAsync(await mod.PostAsJsonAsync($"{Root}/{seed.Submission}/{decision}", new { reviewNote = "Reason" }), HttpStatusCode.Forbidden, "FORBIDDEN");

        await factory.ExecuteDbAsync(async db =>
        {
            var permission = await db.Permissions.SingleAsync(x => x.Code == "MENU_APPROVE");
            await db.RolePermissions.Where(x => x.RoleId == 3 && x.PermissionId == permission.Id).ExecuteDeleteAsync();
            await db.UserPermissions.Where(x => x.UserId == seed.Mod).ExecuteDeleteAsync();
        });
        await ErrorAsync(await mod.GetAsync(Root), HttpStatusCode.Forbidden, "FORBIDDEN");
        await factory.ExecuteDbAsync(async db =>
        {
            var permission = await db.Permissions.SingleAsync(x => x.Code == "MENU_APPROVE");
            db.UserPermissions.Add(new UserPermission { UserId = seed.Mod, PermissionId = permission.Id, Effect = PermissionEffect.Allow });
            await db.SaveChangesAsync();
        });
        (await mod.GetAsync(Root)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var admin = await ClientAsync(seed.Admin);
        (await admin.GetAsync(Root)).StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.ExecuteDbAsync(async db =>
            await db.Users.Where(x => x.Id == seed.Mod).ExecuteUpdateAsync(x => x.SetProperty(u => u.RoleId, (short)1)));
        await ErrorAsync(await mod.GetAsync(Root), HttpStatusCode.Forbidden, "FORBIDDEN");
        await factory.ExecuteDbAsync(async db =>
            await db.Users.Where(x => x.Id == seed.Admin).ExecuteUpdateAsync(x => x.SetProperty(u => u.RoleId, (short)1)));
        await ErrorAsync(await admin.GetAsync(Root), HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    // TEST-06: Return standard envelopes for authentication, role, binding, validation and missing-resource failures.
    /// <summary>Verifies both decision routes and queue filters reject invalid HTTP inputs without side effects.</summary>
    [PostgresEndToEndFact]
    public async Task Errors_ShouldMatchHttpContract()
    {
        var seed = await SeedAsync();
        using var anonymous = factory.CreateClient();
        using var owner = await ClientAsync(seed.Owner);
        using var mod = await ClientAsync(seed.Mod);
        foreach (var path in new[] { Root, $"{Root}/{seed.Submission}" })
        {
            await ErrorAsync(await anonymous.GetAsync(path), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
            await ErrorAsync(await owner.GetAsync(path), HttpStatusCode.Forbidden, "FORBIDDEN");
        }
        foreach (var decision in new[] { "approve", "reject" })
        {
            var path = $"{Root}/{seed.Submission}/{decision}";
            await ErrorAsync(await anonymous.PostAsJsonAsync(path, new { reviewNote = "Reason" }), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
            await ErrorAsync(await owner.PostAsJsonAsync(path, new { reviewNote = "Reason" }), HttpStatusCode.Forbidden, "FORBIDDEN");
            await ErrorAsync(await mod.PostAsync(path, null), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            await ErrorAsync(await mod.PostAsJsonAsync(path, new { reviewNote = new string('x', 1001) }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            await ErrorAsync(await mod.PostAsJsonAsync($"{Root}/0/{decision}", new { reviewNote = "Reason" }), HttpStatusCode.NotFound, "SUBMISSION_NOT_FOUND");
        }
        foreach (var note in new string?[] { null, "", "   " })
            await ErrorAsync(await mod.PostAsJsonAsync($"{Root}/{seed.Submission}/reject", new { reviewNote = note }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        foreach (var query in new[] { "page=0", "pageSize=0", "pageSize=101", "status=draft", "status=cancelled", "restaurantId=0", "page=2147483647&pageSize=100", "page=abc" })
            await ErrorAsync(await mod.GetAsync($"{Root}?{query}"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ErrorAsync(await mod.GetAsync($"{Root}/-1"), HttpStatusCode.NotFound, "SUBMISSION_NOT_FOUND");
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Notifications.CountAsync()).Should().Be(0);
            (await db.AuditLogs.CountAsync(x => x.EntityType == "menu_submission")).Should().Be(0);
            (await db.MenuSubmissions.SingleAsync()).Status.Should().Be(MenuSubmissionStatus.Pending);
        });
    }

    // TEST-07: Reject both decision routes for every non-pending submission state.
    /// <summary>Verifies draft, cancelled, approved and rejected submissions cannot be reviewed again.</summary>
    [PostgresEndToEndFact]
    public async Task Decisions_NonPending_ShouldConflict()
    {
        var seed = await SeedAsync();
        using var mod = await ClientAsync(seed.Mod);
        foreach (var status in new[] { MenuSubmissionStatus.Draft, MenuSubmissionStatus.Approved, MenuSubmissionStatus.Rejected, MenuSubmissionStatus.Cancelled })
        {
            await factory.ExecuteDbAsync(async db =>
                await db.MenuSubmissions.Where(x => x.Id == seed.Submission).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, status)));
            foreach (var decision in new[] { "approve", "reject" })
                await ErrorAsync(await mod.PostAsJsonAsync($"{Root}/{seed.Submission}/{decision}", new { reviewNote = "Reason" }), HttpStatusCode.Conflict, "SUBMISSION_NOT_PENDING");
        }
    }

    // TEST-08: Serialize simultaneous approve/approve, approve/reject and approve/owner-cancel requests.
    /// <summary>Verifies exactly one state transition, with no duplicated audit, notification or menu application.</summary>
    [PostgresEndToEndFact]
    public async Task ConcurrentDecisions_ShouldAllowOneWinner()
    {
        foreach (var competitor in new[] { "approve", "reject", "cancel" })
        {
            var seed = await SeedAsync();
            using var first = await ClientAsync(seed.Mod);
            using var second = await ClientAsync(competitor == "cancel" ? seed.Owner : seed.Admin);
            var secondPath = competitor == "cancel"
                ? $"/api/v1/owner/restaurant/menu-submissions/{seed.Submission}/cancel"
                : $"{Root}/{seed.Submission}/{competitor}";
            var outcomes = await Task.WhenAll(
                first.PostAsJsonAsync($"{Root}/{seed.Submission}/approve", new { }),
                second.PostAsJsonAsync(secondPath, new { reviewNote = "Reason" }));
            outcomes.Count(x => x.StatusCode == HttpStatusCode.OK).Should().Be(1);
            outcomes.Count(x => x.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
            await factory.ExecuteDbAsync(async db =>
            {
                var submission = await db.MenuSubmissions.SingleAsync(x => x.Id == seed.Submission);
                var reviews = submission.Status == MenuSubmissionStatus.Cancelled ? 0 : 1;
                (await db.Notifications.CountAsync()).Should().Be(reviews);
                (await db.AuditLogs.CountAsync(x => x.EntityType == "menu_submission")).Should().Be(reviews);
            });
            foreach (var response in outcomes) response.Dispose();
        }
    }

    // TEST-09: Roll back dishes, generated outbox, notification, audit and submission metadata when persistence fails.
    /// <summary>Forces a real foreign-key error after editing and creating dishes to verify transaction atomicity.</summary>
    [PostgresEndToEndFact]
    public async Task Approve_SaveFailure_ShouldRollBackEverything()
    {
        var seed = await SeedAsync();
        using var mod = await ClientAsync(seed.Mod);
        var before = await MenuSnapshotAsync(seed.Restaurant);
        var baseline = await OutboxCountAsync();
        await factory.ExecuteDbAsync(async db =>
            await db.MenuSubmissionItems.Where(x => x.SubmissionId == seed.Submission && x.SourceDishId == null)
                .ExecuteUpdateAsync(x => x.SetProperty(i => i.TagIds, new short[] { short.MaxValue })));
        using var response = await mod.PostAsJsonAsync($"{Root}/{seed.Submission}/approve", new { });
        await ErrorAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        (await MenuSnapshotAsync(seed.Restaurant)).Should().Be(before);
        (await OutboxCountAsync()).Should().Be(baseline);
        await factory.ExecuteDbAsync(async db =>
        {
            var submission = await db.MenuSubmissions.SingleAsync();
            submission.Status.Should().Be(MenuSubmissionStatus.Pending);
            submission.ReviewedBy.Should().BeNull();
            submission.ReviewedAt.Should().BeNull();
            (await db.MenuSubmissionItems.ToListAsync()).Should().OnlyContain(x => x.ResultDishId == null);
            (await db.Notifications.CountAsync()).Should().Be(0);
            (await db.AuditLogs.CountAsync(x => x.EntityType == "menu_submission")).Should().Be(0);
        });
    }

    // TEST-10: Accept the exact 1000-character boundary and an omitted optional approval note.
    /// <summary>Verifies valid boundary requests succeed through JSON binding and database persistence.</summary>
    [PostgresEndToEndFact]
    public async Task Decisions_ValidNoteBoundaries_ShouldSucceed()
    {
        foreach (var decision in new[] { "approve", "reject" })
        {
            var seed = await SeedAsync();
            using var admin = await ClientAsync(seed.Admin);
            using var response = await admin.PostAsJsonAsync($"{Root}/{seed.Submission}/{decision}", new { reviewNote = new string('x', 1000) });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await DataAsync<MenuSubmissionDto>(response)).ReviewNote.Should().HaveLength(1000);
        }
    }

    // TEST-11: Order submissions with equal timestamps by identifier so consecutive pages neither duplicate nor omit entries.
    /// <summary>Verifies stable SQL pagination and positive restaurant filters across independently owned restaurants.</summary>
    [PostgresEndToEndFact]
    public async Task List_EqualTimestamps_ShouldPaginateStably()
    {
        var seed = await SeedAsync();
        using var mod = await ClientAsync(seed.Mod);
        var secondId = await factory.ExecuteDbAsync(async db =>
        {
            var first = await db.MenuSubmissions.SingleAsync(x => x.Id == seed.Submission);
            var owner = new User
            {
                RoleId = 2, Email = "second-owner@integration.test", DisplayName = "Second owner",
                Status = UserStatus.Active, EmailVerifiedAt = DateTime.UtcNow
            };
            var restaurant = new ANGI.Domain.Entities.Restaurant
            {
                Owner = owner, Name = "Second restaurant", Slug = "second-restaurant", AddressLine = "2 Test street",
                Latitude = 10.8m, Longitude = 106.8m
            };
            db.Restaurants.Add(restaurant);
            await db.SaveChangesAsync();
            var second = new MenuSubmission
            {
                Restaurant = restaurant, CreatedBy = owner.Id, Status = MenuSubmissionStatus.Pending,
                SubmittedAt = first.SubmittedAt
            };
            db.MenuSubmissions.Add(second);
            await db.SaveChangesAsync();
            return second.Id;
        });
        using var firstPage = await mod.GetAsync($"{Root}?page=1&pageSize=1");
        using var secondPage = await mod.GetAsync($"{Root}?page=2&pageSize=1");
        var firstResult = await DataAsync<ANGI.Application.Common.Models.Paged<ModMenuSubmissionCardDto>>(firstPage);
        var secondResult = await DataAsync<ANGI.Application.Common.Models.Paged<ModMenuSubmissionCardDto>>(secondPage);
        firstResult.TotalItems.Should().Be(2);
        firstResult.TotalPages.Should().Be(2);
        firstResult.Items.Single().Id.Should().Be(seed.Submission);
        secondResult.Items.Single().Id.Should().Be(secondId);
        using var filtered = await mod.GetAsync($"{Root}?restaurantId={seed.Restaurant}");
        (await DataAsync<ANGI.Application.Common.Models.Paged<ModMenuSubmissionCardDto>>(filtered))
            .Items.Should().ContainSingle(x => x.Id == seed.Submission);
    }

    /// <summary>Resets the dedicated test schema and seeds users, current dishes and one pending proposed menu.</summary>
    private async Task<Seed> SeedAsync()
    {
        await factory.ResetDatabaseAsync();
        return await factory.ExecuteDbAsync(async db =>
        {
            User User(short role, string name) => new()
            {
                RoleId = role, Email = $"{name}@integration.test", DisplayName = name,
                Status = UserStatus.Active, EmailVerifiedAt = DateTime.UtcNow
            };
            var owner = User(2, "Owner");
            var mod = User(3, "Mod");
            var admin = User(4, "Admin");
            db.Users.AddRange(owner, mod, admin);
            await db.SaveChangesAsync();
            db.Tags.AddRange(new Tag { Id = 1, Code = "lunch", Name = "Lunch", TagType = TagType.MealTime },
                new Tag { Id = 2, Code = "dinner", Name = "Dinner", TagType = TagType.MealTime },
                new Tag { Id = 3, Code = "breakfast", Name = "Breakfast", TagType = TagType.MealTime });
            var restaurant = new ANGI.Domain.Entities.Restaurant
            {
                OwnerId = owner.Id, Name = "Moderated restaurant", Slug = "moderated-restaurant",
                AddressLine = "1 Test street", Latitude = 10.775m, Longitude = 106.7m,
                VerificationStatus = RestaurantVerificationStatus.Verified
            };
            var oldCover = new MediaFile { UploaderId = owner.Id, Url = "https://cdn.test/old.jpg", StorageKey = "image/upload/old.jpg", MimeType = "image/jpeg" };
            var newCover = new MediaFile { UploaderId = owner.Id, Url = "https://cdn.test/new.jpg", StorageKey = "image/upload/new.jpg", MimeType = "image/jpeg" };
            var edited = new Dish { Restaurant = restaurant, Name = "Original dish", Description = "Original description", Price = 100,
                IsAvailable = false, LikeCount = 12, DislikeCount = 3, CoverMedia = oldCover };
            edited.DishTags.Add(new DishTag { TagId = 1 });
            edited.DishTags.Add(new DishTag { TagId = 2 });
            var same = new Dish { Restaurant = restaurant, Name = "Unchanged dish", Price = 200, Status = DishStatus.Hidden };
            var price = new Dish { Restaurant = restaurant, Name = "Price only", Price = 300 };
            var removed = new Dish { Restaurant = restaurant, Name = "Removed dish", Price = 400 };
            var hiddenRemoved = new Dish { Restaurant = restaurant, Name = "Removed hidden dish", Price = 500, Status = DishStatus.Hidden };
            db.Dishes.AddRange(edited, same, price, removed, hiddenRemoved);
            db.MediaFiles.Add(newCover);
            await db.SaveChangesAsync();
            var submission = new MenuSubmission
            {
                Restaurant = restaurant, CreatedBy = owner.Id, Status = MenuSubmissionStatus.Pending,
                SubmittedAt = DateTime.UtcNow, OwnerNote = "Please review",
                Items =
                [
                    new MenuSubmissionItem { SourceDishId = edited.Id, Name = "Edited dish", Description = "Edited description",
                        Price = 150, CoverMediaId = newCover.Id, TagIds = [2, 3], SortOrder = 1 },
                    new MenuSubmissionItem { SourceDishId = same.Id, Name = same.Name, Price = same.Price },
                    new MenuSubmissionItem { SourceDishId = price.Id, Name = price.Name, Price = 350 },
                    new MenuSubmissionItem { Name = "New dish", Description = "New description", Price = 50, TagIds = [1], SortOrder = 2 }
                ]
            };
            db.MenuSubmissions.Add(submission);
            await db.SaveChangesAsync();
            return new Seed(owner.Id, mod.Id, admin.Id, restaurant.Id, submission.Id, edited.Id, same.Id, price.Id, removed.Id, hiddenRemoved.Id);
        });
    }

    /// <summary>Creates a real signed access token for the seeded database user.</summary>
    private async Task<HttpClient> ClientAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ANGI.Infrastructure.Persistences.ANGIContext>();
        var user = await db.Users.AsNoTracking().Include(x => x.Role).SingleAsync(x => x.Id == userId);
        var token = scope.ServiceProvider.GetRequiredService<IAuthenticationTokenService>().CreateAccessToken(user, 1);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    /// <summary>Reads a successful response envelope and checks its common fields.</summary>
    private static async Task<T> DataAsync<T>(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        envelope!.Success.Should().BeTrue();
        envelope.ErrorCode.Should().BeNull();
        return envelope.Data!;
    }

    /// <summary>Checks the status and common error envelope without exposing internal exception details.</summary>
    private static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
            var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>();
            envelope!.Success.Should().BeFalse();
            envelope.ErrorCode.Should().Be(code);
            envelope.Data.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    /// <summary>Counts persisted outbox messages to detect unexpected or duplicate events.</summary>
    private Task<int> OutboxCountAsync() => factory.ExecuteDbAsync(db => db.OutboxMessages.CountAsync());

    /// <summary>Captures all mutable live-menu fields and tag links for rollback and rejection assertions.</summary>
    private Task<string> MenuSnapshotAsync(long restaurantId) => factory.ExecuteDbAsync(async db =>
    {
        var dishes = await db.Dishes.AsNoTracking().Include(x => x.DishTags).Where(x => x.RestaurantId == restaurantId).OrderBy(x => x.Id).ToListAsync();
        return JsonSerializer.Serialize(dishes.Select(x => new
        {
            x.Id, x.Name, x.Description, x.DishForm, x.Price, x.Status, x.CoverMediaId,
            x.IsAvailable, x.LikeCount, x.DislikeCount, x.SortOrder,
            Tags = x.DishTags.Select(t => t.TagId).OrderBy(id => id).ToArray()
        }));
    });

    /// <summary>Identifies the independently persisted actors, menu and dishes used by each scenario.</summary>
    private sealed record Seed(int Owner, int Mod, int Admin, long Restaurant, long Submission,
        int Edited, int Unchanged, int PriceOnly, int Removed, int HiddenRemoved);
}
