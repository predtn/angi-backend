using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.Common.Models;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Exercises roadmap tasks through the real HTTP pipeline, EF Core repository, and PostgreSQL.</summary>
public sealed class RoadmapEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private const string Password = "Password123";
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public RoadmapEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Create a pinned roadmap, shrink it, and switch to gps through HTTP and PostgreSQL.
    /// <summary>Verifies RM-01 and RM-04 persist days and maintain the origin invariant end to end.</summary>
    [PostgresEndToEndFact]
    public async Task CreateAndUpdateRoadmap_ShouldPersistDaysAndOriginInvariant()
    {
        // STEP-01: Reset the test database.
        await _factory.ResetDatabaseAsync();
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain auth through RegisterTravelerAndLoginAsync.
        var auth = await RegisterTravelerAndLoginAsync(client, "roadmap-owner@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, auth.AccessToken);

        // STEP-05: Send POST /api/v1/roadmaps with a CreateRoadmapRequestDto body and keep createResponse.
        using var createResponse = await client.PostAsJsonAsync("/api/v1/roadmaps", new CreateRoadmapRequestDto
        {
            Title = "Integration trip",
            StartDate = new DateOnly(2026, 10, 20),
            NumDays = 3,
            BudgetAmount = 1_500_000,
            OriginMode = "pinned",
            OriginLatitude = 10.775m,
            OriginLongitude = 106.700m,
            OriginLabel = "Integration Hotel"
        });

        // STEP-06: Obtain created through SuccessDataAsync; assert createResponse.StatusCode is HttpStatusCode.Created and created.Itinerary.Select(filtered) equals 1.
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());
        var created = await SuccessDataAsync<RoadmapDetailDto>(createResponse);
        created.Itinerary.Select(day => day.DayNumber).Should().Equal(1, 2, 3);

        // STEP-07: Send PATCH /api/v1/roadmaps/{created.Overview.Id} with a body of title, numDays and originMode and keep updateResponse.
        using var updateResponse = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{created.Overview.Id}", new
        {
            title = "Updated integration trip",
            numDays = 2,
            originMode = "gps"
        });

        // STEP-08: Obtain updated through SuccessDataAsync; assert updateResponse.StatusCode is HttpStatusCode.OK.
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK, await updateResponse.Content.ReadAsStringAsync());
        var updated = await SuccessDataAsync<RoadmapDetailDto>(updateResponse);
        // STEP-09: Assert updated.Overview.Title is "Updated integration trip", updated.Overview.OriginMode is "gps", updated.Overview.OriginLatitude is null and 1 more.
        updated.Overview.Title.Should().Be("Updated integration trip");
        updated.Overview.OriginMode.Should().Be("gps");
        updated.Overview.OriginLatitude.Should().BeNull();
        updated.Itinerary.Select(day => day.DayNumber).Should().Equal(1, 2);
        // STEP-10: Assert in the database persisted.UserId is auth.User.Id, persisted.NumDays is 2, persisted.Days.Select(filtered).Order() equals 1 and 4 more.
        await _factory.ExecuteDbAsync(async context =>
        {
            var persisted = await context.Roadmaps
                .Include(roadmap => roadmap.Days)
                .SingleAsync(roadmap => roadmap.Id == created.Overview.Id);
            persisted.UserId.Should().Be(auth.User.Id);
            persisted.NumDays.Should().Be(2);
            persisted.Days.Select(day => day.DayNumber).Order().Should().Equal(1, 2);
            persisted.OriginMode.Should().Be(RoadmapOriginMode.Gps);
            persisted.OriginLatitude.Should().BeNull();
            persisted.OriginLongitude.Should().BeNull();
            persisted.OriginLabel.Should().BeNull();
        });
    }

    // TEST-02: Hide another traveler's roadmap behind ROADMAP_NOT_FOUND for every implemented owned endpoint.
    /// <summary>Verifies RM-03, RM-04, RM-05, and RM-14 ownership filtering through HTTP and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task AccessAnotherTravelersRoadmap_ShouldReturnNotFoundAcrossOwnedEndpoints()
    {
        // STEP-01: Reset the test database.
        await _factory.ResetDatabaseAsync();
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain first through RegisterTravelerAndLoginAsync.
        var first = await RegisterTravelerAndLoginAsync(client, "first-roadmap@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, first.AccessToken);
        // STEP-05: Send POST /api/v1/roadmaps with a CreateRoadmapRequestDto body and keep createResponse.
        using var createResponse = await client.PostAsJsonAsync("/api/v1/roadmaps", new CreateRoadmapRequestDto
        {
            Title = "Private trip",
            NumDays = 1
        });
        // STEP-06: Obtain created through SuccessDataAsync and second through RegisterTravelerAndLoginAsync.
        var created = await SuccessDataAsync<RoadmapDetailDto>(createResponse);
        var second = await RegisterTravelerAndLoginAsync(client, "second-roadmap@integration.test");
        // STEP-07: Authenticate the HTTP client through Authorize.
        Authorize(client, second.AccessToken);

        // STEP-08: Send PATCH /api/v1/roadmaps/{created.Overview.Id} with a body of title and keep updateResponse.
        using var updateResponse = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{created.Overview.Id}", new
        {
            title = "Unauthorized change"
        });
        // STEP-09: Send GET /api/v1/roadmaps/{created.Overview.Id} and keep detailResponse.
        using var detailResponse = await client.GetAsync($"/api/v1/roadmaps/{created.Overview.Id}");
        // STEP-10: Send GET /api/v1/roadmaps/{created.Overview.Id}/days/{created.Itinerary.Single().DayId}/route and keep routeResponse.
        using var routeResponse = await client.GetAsync(
            $"/api/v1/roadmaps/{created.Overview.Id}/days/{created.Itinerary.Single().DayId}/route");
        // STEP-11: Send DELETE /api/v1/roadmaps/{created.Overview.Id} and keep deleteResponse.
        using var deleteResponse = await client.DeleteAsync($"/api/v1/roadmaps/{created.Overview.Id}");

        // STEP-12: Assert for each response, response.StatusCode is HttpStatusCode.NotFound and error.ErrorCode is "ROADMAP_NOT_FOUND".
        foreach (var response in new[] { updateResponse, detailResponse, routeResponse, deleteResponse })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            error!.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        }
    }

    // TEST-03: Page only the caller's visible roadmaps and compute every RoadmapSummary field from current data.
    /// <summary>Verifies RM-02 ownership, soft-delete, paging, cost, item count, cover, and validation end to end.</summary>
    [PostgresEndToEndFact]
    public async Task GetMyRoadmaps_ShouldReturnOnlyOwnedVisibleComputedSummaries()
    {
        // STEP-01: Reset the test database and get publicDish from DiscoverySeed.PublicDishAsync.
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain owner through RegisterTravelerAndLoginAsync.
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-list-owner@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, owner.AccessToken);
        // STEP-05: Obtain oldest through CreateRoadmapAsync, deleted through CreateRoadmapAsync, newest through CreateRoadmapAsync and 1 more.
        var oldest = await CreateRoadmapAsync(client, "Oldest trip", new DateOnly(2026, 10, 10), 3);
        var deleted = await CreateRoadmapAsync(client, "Deleted trip", null, 1);
        var newest = await CreateRoadmapAsync(client, "Newest trip", null, 1);

        var anotherTraveler = await RegisterTravelerAndLoginAsync(client, "roadmap-list-other@integration.test");
        // STEP-06: Authenticate the HTTP client through Authorize.
        Authorize(client, anotherTraveler.AccessToken);
        // STEP-07: Assert CreateRoadmapAsync confirms Another traveler's trip.
        await CreateRoadmapAsync(client, "Another traveler's trip", null, 1);

        // STEP-08: Seed or update rows directly in the test database and authenticate the HTTP client through Authorize.
        await _factory.ExecuteDbAsync(async context =>
        {
            var oldestDayId = await context.RoadmapDays
                .Where(day => day.RoadmapId == oldest.Overview.Id && day.DayNumber == 1)
                .Select(day => day.Id)
                .SingleAsync();
            context.RoadmapItems.Add(new RoadmapItem
            {
                RoadmapDayId = oldestDayId,
                DishId = publicDish.DishId,
                MealSlot = MealSlot.Breakfast,
                SortOrder = 0,
                Source = RoadmapItemSource.Manual
            });
            var deletedRoadmap = await context.Roadmaps.SingleAsync(item => item.Id == deleted.Overview.Id);
            deletedRoadmap.DeletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        });
        Authorize(client, owner.AccessToken);

        // STEP-09: Send GET /api/v1/roadmaps?page=1&pageSize=1 and keep firstPageResponse.
        using var firstPageResponse = await client.GetAsync("/api/v1/roadmaps?page=1&pageSize=1");
        // STEP-10: Obtain firstPage through SuccessDataAsync; assert firstPageResponse.StatusCode is HttpStatusCode.OK and 4 more.
        firstPageResponse.StatusCode.Should().Be(HttpStatusCode.OK, await firstPageResponse.Content.ReadAsStringAsync());
        var firstPage = await SuccessDataAsync<Paged<RoadmapSummaryDto>>(firstPageResponse);
        firstPage.Items.Should().ContainSingle().Which.Id.Should().Be(newest.Overview.Id);
        firstPage.TotalItems.Should().Be(2);
        firstPage.TotalPages.Should().Be(2);

        // STEP-11: Send GET /api/v1/roadmaps?page=2&pageSize=1 and keep secondPageResponse.
        using var secondPageResponse = await client.GetAsync("/api/v1/roadmaps?page=2&pageSize=1");
        // STEP-12: Obtain secondPage through SuccessDataAsync; assert secondPage.Items has exactly one matching item.
        var secondPage = await SuccessDataAsync<Paged<RoadmapSummaryDto>>(secondPageResponse);
        var summary = secondPage.Items.Should().ContainSingle().Subject;
        // STEP-13: Assert summary.Id is oldest.Overview.Id, summary.EndDate is a new DateOnly, summary.ItemCount is 1, summary.TotalEstimatedCost is 45_000 and 1 more.
        summary.Id.Should().Be(oldest.Overview.Id);
        summary.EndDate.Should().Be(new DateOnly(2026, 10, 12));
        summary.ItemCount.Should().Be(1);
        summary.TotalEstimatedCost.Should().Be(45_000);
        summary.CoverUrl.Should().Be("https://cdn.test/dishes/cover.jpg");

        // STEP-14: Send GET /api/v1/roadmaps?page=3&pageSize=1 and keep emptyPageResponse.
        using var emptyPageResponse = await client.GetAsync("/api/v1/roadmaps?page=3&pageSize=1");
        // STEP-15: Obtain emptyPage through SuccessDataAsync; assert emptyPage.Items is empty and emptyPage.TotalItems is 2.
        var emptyPage = await SuccessDataAsync<Paged<RoadmapSummaryDto>>(emptyPageResponse);
        emptyPage.Items.Should().BeEmpty();
        emptyPage.TotalItems.Should().Be(2);

        // STEP-16: Send GET /api/v1/roadmaps?page=0&pageSize=101 and keep invalidResponse.
        using var invalidResponse = await client.GetAsync("/api/v1/roadmaps?page=0&pageSize=101");
        // STEP-17: Read invalid from the response; assert invalidResponse.StatusCode is HttpStatusCode.BadRequest and invalid.ErrorCode is "VALIDATION_FAILED".
        invalidResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var invalid = await invalidResponse.Content.ReadFromJsonAsync<ApiResponse<object>>();
        invalid!.ErrorCode.Should().Be("VALIDATION_FAILED");
    }

    // TEST-04: Read current dish data, item feedback, itinerary ordering, and calculated budget through RM-03.
    /// <summary>Verifies the real RM-03 repository graph and mapper against PostgreSQL through the HTTP pipeline.</summary>
    [PostgresEndToEndFact]
    public async Task GetRoadmapDetail_ShouldReturnOwnedCurrentCalculatedAggregate()
    {
        // STEP-01: Reset the test database and get publicDish from DiscoverySeed.PublicDishAsync.
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain traveler through RegisterTravelerAndLoginAsync.
        var traveler = await RegisterTravelerAndLoginAsync(client, "roadmap-detail@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, traveler.AccessToken);
        // STEP-05: Obtain roadmap through CreateRoadmapAsync.
        var roadmap = await CreateRoadmapAsync(client, "Detail trip", new DateOnly(2026, 10, 20), 2);
        // STEP-06: Seed or update rows directly in the test database.
        await _factory.ExecuteDbAsync(async context =>
        {
            var removedDish = await context.Dishes.SingleAsync(dish => dish.Id == publicDish.HiddenDishId);
            removedDish.Status = DishStatus.Removed;
            var day = await context.RoadmapDays.SingleAsync(value =>
                value.RoadmapId == roadmap.Overview.Id && value.DayNumber == 1);
            var dinner = new RoadmapItem
            {
                RoadmapDayId = day.Id,
                DishId = publicDish.HiddenDishId,
                MealSlot = MealSlot.Dinner,
                SortOrder = 0,
                Source = RoadmapItemSource.Manual
            };
            var breakfast = new RoadmapItem
            {
                RoadmapDayId = day.Id,
                DishId = publicDish.DishId,
                MealSlot = MealSlot.Breakfast,
                SortOrder = 1,
                Source = RoadmapItemSource.Manual
            };
            context.RoadmapItems.AddRange(dinner, breakfast);
            await context.SaveChangesAsync();
            context.DishFeedbacks.Add(new DishFeedback
            {
                EventUuid = Guid.NewGuid(),
                UserId = traveler.User.Id,
                DishId = publicDish.DishId,
                Value = FeedbackValue.Like,
                RoadmapItemId = breakfast.Id
            });
            await context.SaveChangesAsync();
        });

        // STEP-07: Send GET /api/v1/roadmaps/{roadmap.Overview.Id} and keep response.
        using var response = await client.GetAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");

        // STEP-08: Obtain detail through SuccessDataAsync; assert response.StatusCode is HttpStatusCode.OK.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var detail = await SuccessDataAsync<RoadmapDetailDto>(response);
        // STEP-09: Assert detail.Itinerary has 2 item(s), detail.Itinerary[0].Items.Select(filtered) equals "breakfast" and 5 more.
        detail.Itinerary.Should().HaveCount(2);
        detail.Itinerary[0].Items.Select(item => item.MealSlot).Should().Equal("breakfast", "dinner");
        detail.Itinerary[0].Items[0].MyFeedback.Should().Be("like");
        detail.Itinerary[0].Items[1].Dish.Status.Should().Be("removed");
        detail.Overview.TotalEstimatedCost.Should().Be(75_000);
        detail.Overview.CoverUrl.Should().Be("https://cdn.test/dishes/cover.jpg");
        detail.Budget.ByDay.Select(item => item.Cost).Should().Equal(75_000, 0);
    }

    // TEST-05: Calculate a daily route through the real repository and Mapbox fallback service.
    /// <summary>Verifies RM-14 GPS precedence, item identifiers, fallback totals, and missing-day error end to end.</summary>
    [PostgresEndToEndFact]
    public async Task GetRoadmapDayRoute_ShouldReturnEstimatedGpsRouteAndDayNotFound()
    {
        // STEP-01: Reset the test database and get publicDish from DiscoverySeed.PublicDishAsync.
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain traveler through RegisterTravelerAndLoginAsync.
        var traveler = await RegisterTravelerAndLoginAsync(client, "roadmap-route@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, traveler.AccessToken);
        // STEP-05: Send POST /api/v1/roadmaps with a CreateRoadmapRequestDto body and keep createResponse.
        using var createResponse = await client.PostAsJsonAsync("/api/v1/roadmaps", new CreateRoadmapRequestDto
        {
            Title = "Route trip",
            NumDays = 1,
            OriginMode = "pinned",
            OriginLatitude = 11m,
            OriginLongitude = 107m,
            OriginLabel = "Pinned point"
        });
        // STEP-06: Obtain roadmap through SuccessDataAsync.
        var roadmap = await SuccessDataAsync<RoadmapDetailDto>(createResponse);
        // STEP-07: Get itemIds from _factory.ExecuteDbAsync.
        var itemIds = await _factory.ExecuteDbAsync(async context =>
        {
            var day = await context.RoadmapDays.SingleAsync(value => value.RoadmapId == roadmap.Overview.Id);
            var first = new RoadmapItem
            {
                RoadmapDayId = day.Id,
                DishId = publicDish.DishId,
                MealSlot = MealSlot.Breakfast,
                SortOrder = 0,
                Source = RoadmapItemSource.Manual
            };
            var second = new RoadmapItem
            {
                RoadmapDayId = day.Id,
                DishId = publicDish.HiddenDishId,
                MealSlot = MealSlot.Lunch,
                SortOrder = 0,
                Source = RoadmapItemSource.Manual
            };
            context.RoadmapItems.AddRange(first, second);
            await context.SaveChangesAsync();
            return (DayId: day.Id, FirstItemId: first.Id, SecondItemId: second.Id);
        });

        // STEP-08: Send GET /api/v1/roadmaps/{roadmap.Overview.Id}/days/{itemIds.DayId}/route?originLat=10.775&originLng=106.700&profile=walking and keep response.
        using var response = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{itemIds.DayId}/route" +
            "?originLat=10.775&originLng=106.700&profile=walking");

        // STEP-09: Obtain route through SuccessDataAsync; assert response.StatusCode is HttpStatusCode.OK.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var route = await SuccessDataAsync<GetRoadmapDayRouteResponseDto>(response);
        // STEP-10: Assert route.Profile is "walking", route.Origin is not null, route.Origin.Source is "gps", route.OriginSkipped is false and 4 more.
        route.Profile.Should().Be("walking");
        route.Origin.Should().NotBeNull();
        route.Origin!.Source.Should().Be("gps");
        route.OriginSkipped.Should().BeFalse();
        route.Legs.Select(leg => (leg.FromItemId, leg.ToItemId)).Should().Equal(
            ((long?)null, itemIds.FirstItemId), ((long?)itemIds.FirstItemId, itemIds.SecondItemId));
        route.IsEstimated.Should().BeTrue();
        route.TotalDistanceMeters.Should().Be(0);
        route.TotalDurationSeconds.Should().Be(0);

        // STEP-11: Send GET /api/v1/roadmaps/{roadmap.Overview.Id}/days/999999/route and keep missingDay.
        using var missingDay = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/999999/route");
        // STEP-12: Read error from the response; assert missingDay.StatusCode is HttpStatusCode.NotFound and error.ErrorCode is "DAY_NOT_FOUND".
        missingDay.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await missingDay.Content.ReadFromJsonAsync<ApiResponse<object>>();
        error!.ErrorCode.Should().Be("DAY_NOT_FOUND");
    }

    // TEST-06: Soft-delete only the owned roadmap while preserving its child records.
    /// <summary>Verifies RM-05 persistence, response, visibility, and repeated-delete behavior end to end.</summary>
    [PostgresEndToEndFact]
    public async Task DeleteRoadmap_ShouldSoftDeleteAndHideOnlyTheOwnedRoadmap()
    {
        // STEP-01: Reset the test database and get publicDish from DiscoverySeed.PublicDishAsync.
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain traveler through RegisterTravelerAndLoginAsync.
        var traveler = await RegisterTravelerAndLoginAsync(client, "roadmap-delete@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, traveler.AccessToken);
        // STEP-05: Obtain roadmap through CreateRoadmapAsync.
        var roadmap = await CreateRoadmapAsync(client, "Delete trip", null, 1);
        // STEP-06: Get relatedIds from _factory.ExecuteDbAsync.
        var relatedIds = await _factory.ExecuteDbAsync(async context =>
        {
            var dayId = await context.RoadmapDays
                .Where(day => day.RoadmapId == roadmap.Overview.Id)
                .Select(day => day.Id)
                .SingleAsync();
            var item = new RoadmapItem
            {
                RoadmapDayId = dayId,
                DishId = publicDish.DishId,
                MealSlot = MealSlot.Lunch,
                SortOrder = 0,
                Source = RoadmapItemSource.Manual
            };
            context.RoadmapItems.Add(item);
            var blog = new Blog
            {
                AuthorId = traveler.User.Id,
                RoadmapId = roadmap.Overview.Id,
                RoadmapSnapshot = "{\"title\":\"Delete trip\"}",
                Title = "Shared delete trip",
                Slug = "shared-delete-trip",
                Content = "The snapshot must remain available.",
                PublishedAt = DateTime.UtcNow
            };
            context.Blogs.Add(blog);
            await context.SaveChangesAsync();
            return (ItemId: item.Id, BlogId: blog.Id);
        });

        // STEP-07: Send DELETE /api/v1/roadmaps/{roadmap.Overview.Id} and keep deleteResponse.
        using var deleteResponse = await client.DeleteAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");

        // STEP-08: Read envelope from the response; assert deleteResponse.StatusCode is HttpStatusCode.OK, envelope is not null, envelope.Success is true and 1 more.
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK, await deleteResponse.Content.ReadAsStringAsync());
        var envelope = await deleteResponse.Content.ReadFromJsonAsync<ApiResponse<object>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().BeNull();
        // STEP-09: Assert in the database context.Roadmaps.AnyAsync(filtered) is false, deleted.UserId is traveler.User.Id, deleted.DeletedAt is not null and 4 more.
        await _factory.ExecuteDbAsync(async context =>
        {
            (await context.Roadmaps.AnyAsync(item => item.Id == roadmap.Overview.Id)).Should().BeFalse();
            var deleted = await context.Roadmaps
                .IgnoreQueryFilters()
                .SingleAsync(item => item.Id == roadmap.Overview.Id);
            deleted.UserId.Should().Be(traveler.User.Id);
            deleted.DeletedAt.Should().NotBeNull();
            (await context.RoadmapDays.CountAsync(day => day.RoadmapId == roadmap.Overview.Id)).Should().Be(1);
            (await context.RoadmapItems.AnyAsync(item => item.Id == relatedIds.ItemId)).Should().BeTrue();
            var sharedBlog = await context.Blogs.SingleAsync(blog => blog.Id == relatedIds.BlogId);
            sharedBlog.RoadmapId.Should().Be(roadmap.Overview.Id);
            using var snapshot = JsonDocument.Parse(sharedBlog.RoadmapSnapshot!);
            snapshot.RootElement.GetProperty("title").GetString().Should().Be("Delete trip");
        });

        // STEP-10: Send GET /api/v1/roadmaps/{roadmap.Overview.Id} and keep detailResponse.
        using var detailResponse = await client.GetAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");
        // STEP-11: Send DELETE /api/v1/roadmaps/{roadmap.Overview.Id} and keep repeatedDeleteResponse.
        using var repeatedDeleteResponse = await client.DeleteAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");
        // STEP-12: Assert for each response, response.StatusCode is HttpStatusCode.NotFound and error.ErrorCode is "ROADMAP_NOT_FOUND".
        foreach (var response in new[] { detailResponse, repeatedDeleteResponse })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            error!.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        }

        // STEP-13: Send GET /api/v1/roadmaps and keep listResponse.
        using var listResponse = await client.GetAsync("/api/v1/roadmaps");
        // STEP-14: Obtain list through SuccessDataAsync; assert list.Items holds no matching item.
        var list = await SuccessDataAsync<Paged<RoadmapSummaryDto>>(listResponse);
        list.Items.Should().NotContain(item => item.Id == roadmap.Overview.Id);
    }

    // TEST-07: Suggest an eligible dish, add it at the end of a meal, then exclude it from later suggestions.
    /// <summary>Verifies RM-06 and RM-07 together through HTTP, real repositories, PostgreSQL, and reco fallback.</summary>
    [PostgresEndToEndFact]
    public async Task SuggestAndAddRoadmapItem_ShouldPersistAndExcludeExistingDish()
    {
        // STEP-01: Reset the test database and get publicDish from DiscoverySeed.PublicDishAsync.
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain traveler through RegisterTravelerAndLoginAsync.
        var traveler = await RegisterTravelerAndLoginAsync(client, "roadmap-add-item@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, traveler.AccessToken);
        // STEP-05: Send POST /api/v1/roadmaps with a CreateRoadmapRequestDto body and keep createResponse.
        using var createResponse = await client.PostAsJsonAsync("/api/v1/roadmaps", new CreateRoadmapRequestDto
        {
            Title = "Manual food trip",
            NumDays = 1,
            OriginMode = "pinned",
            OriginLatitude = 10.775m,
            OriginLongitude = 106.700m,
            OriginLabel = "Hotel"
        });
        // STEP-06: Obtain roadmap through SuccessDataAsync.
        var roadmap = await SuccessDataAsync<RoadmapDetailDto>(createResponse);
        // STEP-07: Derive dayId from roadmap.Itinerary.Single().DayId and seed or update rows directly in the test database.
        var dayId = roadmap.Itinerary.Single().DayId;
        await _factory.ExecuteDbAsync(async context =>
        {
            context.RoadmapItems.Add(new RoadmapItem
            {
                RoadmapDayId = dayId,
                DishId = publicDish.HiddenDishId,
                MealSlot = MealSlot.Lunch,
                SortOrder = 4,
                Source = RoadmapItemSource.Manual
            });
            await context.SaveChangesAsync();
        });

        // STEP-08: Send GET /api/v1/roadmaps/{roadmap.Overview.Id}/suggestions (with query parameters) and keep suggestionsResponse.
        using var suggestionsResponse = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/suggestions?province=Ho%20Chi%20Minh%20City&mealSlot=lunch&limit=1");

        // STEP-09: Obtain suggestions through SuccessDataAsync; assert suggestionsResponse.StatusCode is HttpStatusCode.OK, suggestions.RequestId is null and 4 more.
        suggestionsResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await suggestionsResponse.Content.ReadAsStringAsync());
        var suggestions = await SuccessDataAsync<GetRoadmapSuggestionsResponseDto>(suggestionsResponse);
        suggestions.RequestId.Should().BeNull();
        suggestions.Items.Should().ContainSingle().Which.Id.Should().Be(publicDish.DishId);
        suggestions.Items.Single().Rank.Should().Be(1);
        suggestions.NextCursor.Should().BeNull();

        // STEP-10: Send POST /api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items with a AddRoadmapItemRequestDto body and keep addResponse.
        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });

        // STEP-11: Obtain added through SuccessDataAsync; assert addResponse.StatusCode is HttpStatusCode.Created.
        addResponse.StatusCode.Should().Be(HttpStatusCode.Created, await addResponse.Content.ReadAsStringAsync());
        var added = await SuccessDataAsync<RoadmapItemDto>(addResponse);
        // STEP-12: Assert added.DayId is dayId, added.DayNumber is 1, added.SortOrder is 5, added.Source is "manual" and 4 more.
        added.DayId.Should().Be(dayId);
        added.DayNumber.Should().Be(1);
        added.SortOrder.Should().Be(5);
        added.Source.Should().Be("manual");
        added.Dish.Id.Should().Be(publicDish.DishId);
        added.Dish.Status.Should().Be("active");
        added.Dish.Tags.Should().ContainSingle(item => item.Code == "lunch");
        added.MyFeedback.Should().BeNull();
        // STEP-13: Assert in the database persisted.RoadmapDayId is dayId, persisted.DishId is publicDish.DishId, persisted.MealSlot is MealSlot.Lunch and 2 more.
        await _factory.ExecuteDbAsync(async context =>
        {
            var persisted = await context.RoadmapItems.SingleAsync(item => item.Id == added.Id);
            persisted.RoadmapDayId.Should().Be(dayId);
            persisted.DishId.Should().Be(publicDish.DishId);
            persisted.MealSlot.Should().Be(MealSlot.Lunch);
            persisted.SortOrder.Should().Be(5);
            persisted.Source.Should().Be(RoadmapItemSource.Manual);
        });

        // STEP-14: Send GET /api/v1/roadmaps/{roadmap.Overview.Id}/suggestions (with query parameters) and keep repeatedResponse.
        using var repeatedResponse = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/suggestions?province=Ho%20Chi%20Minh%20City&mealSlot=lunch");
        // STEP-15: Obtain repeated through SuccessDataAsync; assert repeated.Items is empty.
        var repeated = await SuccessDataAsync<GetRoadmapSuggestionsResponseDto>(repeatedResponse);
        repeated.Items.Should().BeEmpty();
    }

    // TEST-08: Enforce ownership, day membership, and dish availability at the real RM-07 boundary.
    /// <summary>Verifies every documented RM-07 business error through HTTP and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task AddRoadmapItem_WithInvalidTargets_ShouldReturnDocumentedErrors()
    {
        // STEP-01: Reset the test database and get publicDish from DiscoverySeed.PublicDishAsync.
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain owner through RegisterTravelerAndLoginAsync.
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-item-errors-owner@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, owner.AccessToken);
        // STEP-05: Obtain roadmap through CreateRoadmapAsync.
        var roadmap = await CreateRoadmapAsync(client, "Protected trip", null, 1);
        // STEP-06: Derive dayId from roadmap.Itinerary.Single().DayId.
        var dayId = roadmap.Itinerary.Single().DayId;

        // STEP-07: Send POST /api/v1/roadmaps/{roadmap.Overview.Id}/days/999999/items with a AddRoadmapItemRequestDto body and keep missingDay.
        using var missingDay = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/999999/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        // STEP-08: Assert missingDay.StatusCode is HttpStatusCode.NotFound and the response body.ErrorCode is "DAY_NOT_FOUND".
        missingDay.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missingDay.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("DAY_NOT_FOUND");

        // STEP-09: Send POST /api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items with a AddRoadmapItemRequestDto body and keep hiddenDish.
        using var hiddenDish = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.HiddenDishId, MealSlot = "lunch" });
        // STEP-10: Assert hiddenDish.StatusCode is HttpStatusCode.BadRequest and the response body.ErrorCode is "DISH_NOT_AVAILABLE".
        hiddenDish.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await hiddenDish.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("DISH_NOT_AVAILABLE");

        // STEP-11: Seed or update rows directly in the test database.
        await _factory.ExecuteDbAsync(async context =>
        {
            var dish = await context.Dishes.SingleAsync(item => item.Id == publicDish.DishId);
            dish.IsAvailable = false;
            await context.SaveChangesAsync();
        });
        // STEP-12: Send POST /api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items with a AddRoadmapItemRequestDto body and keep soldOutDish.
        using var soldOutDish = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        // STEP-13: Assert soldOutDish.StatusCode is HttpStatusCode.BadRequest and the response body.ErrorCode is "DISH_NOT_AVAILABLE".
        soldOutDish.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await soldOutDish.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("DISH_NOT_AVAILABLE");

        // STEP-14: Seed or update rows directly in the test database.
        await _factory.ExecuteDbAsync(async context =>
        {
            var dish = await context.Dishes
                .Include(item => item.Restaurant)
                .SingleAsync(item => item.Id == publicDish.DishId);
            dish.IsAvailable = true;
            dish.Restaurant.OperatingStatus = RestaurantOperatingStatus.TemporarilyClosed;
            await context.SaveChangesAsync();
        });
        // STEP-15: Send POST /api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items with a AddRoadmapItemRequestDto body and keep closedRestaurant.
        using var closedRestaurant = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        // STEP-16: Obtain other through RegisterTravelerAndLoginAsync; assert closedRestaurant.StatusCode is HttpStatusCode.BadRequest and 1 more.
        closedRestaurant.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await closedRestaurant.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("DISH_NOT_AVAILABLE");

        var other = await RegisterTravelerAndLoginAsync(client, "roadmap-item-errors-other@integration.test");
        // STEP-17: Authenticate the HTTP client through Authorize.
        Authorize(client, other.AccessToken);
        // STEP-18: Send POST /api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items with a AddRoadmapItemRequestDto body and keep foreignRoadmap.
        using var foreignRoadmap = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        // STEP-19: Assert foreignRoadmap.StatusCode is HttpStatusCode.NotFound and the response body.ErrorCode is "ROADMAP_NOT_FOUND".
        foreignRoadmap.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreignRoadmap.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_NOT_FOUND");
    }

    // TEST-09: Read full RM-12 detail and conceal every invalid ownership or membership boundary.
    /// <summary>Verifies item, inactive dish, restaurant, feedback, moderated own review, and all 404 scopes in PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task GetRoadmapItemDetail_ShouldReturnCompleteOwnedDataAndHideInvalidTargets()
    {
        // STEP-01: Reset the test database and get publicDish from DiscoverySeed.PublicDishAsync.
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain owner through RegisterTravelerAndLoginAsync.
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-item-detail-owner@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, owner.AccessToken);
        // STEP-05: Obtain roadmap through CreateRoadmapAsync.
        var roadmap = await CreateRoadmapAsync(client, "Item detail trip", null, 1);
        // STEP-06: Derive dayId from roadmap.Itinerary.Single().DayId.
        var dayId = roadmap.Itinerary.Single().DayId;
        // STEP-07: Send POST /api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items with a AddRoadmapItemRequestDto body and keep addResponse.
        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        // STEP-08: Obtain added through SuccessDataAsync.
        var added = await SuccessDataAsync<RoadmapItemDto>(addResponse);

        // STEP-09: Seed or update rows directly in the test database.
        await _factory.ExecuteDbAsync(async context =>
        {
            var dish = await context.Dishes.SingleAsync(value => value.Id == publicDish.DishId);
            dish.Status = DishStatus.Hidden;
            dish.IsAvailable = false;
            var restaurant = await context.Restaurants.SingleAsync(value => value.Id == publicDish.RestaurantId);
            restaurant.BusinessHours.Add(new RestaurantBusinessHour
            {
                DayOfWeek = 1,
                OpenTime = new TimeOnly(6, 0),
                CloseTime = new TimeOnly(13, 30)
            });
            restaurant.Images.Add(new RestaurantImage
            {
                SortOrder = 0,
                Media = TestMedia(owner.User.Id, "restaurant-detail")
            });
            context.DishFeedbacks.Add(new DishFeedback
            {
                EventUuid = Guid.NewGuid(),
                UserId = owner.User.Id,
                DishId = dish.Id,
                RoadmapItemId = added.Id,
                Value = FeedbackValue.Dislike
            });
            var review = new RestaurantReview
            {
                RestaurantId = restaurant.Id,
                UserId = owner.User.Id,
                Rating = 4,
                Content = "Own moderated review",
                RoadmapItemId = added.Id,
                Status = ContentStatus.Hidden
            };
            review.Images.Add(new ReviewImage { Media = TestMedia(owner.User.Id, "review-detail") });
            context.RestaurantReviews.Add(review);
            await context.SaveChangesAsync();
        });

        // STEP-10: Send GET /api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id} and keep detailResponse.
        using var detailResponse = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");

        // STEP-11: Obtain detail through SuccessDataAsync; assert detailResponse.StatusCode is HttpStatusCode.OK.
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK, await detailResponse.Content.ReadAsStringAsync());
        var detail = await SuccessDataAsync<RoadmapItemDetailDto>(detailResponse);
        // STEP-12: Assert detail.Id is added.Id, detail.DayId is dayId, detail.MyFeedback is "dislike", detail.Dish.Id is publicDish.DishId and 13 more.
        detail.Id.Should().Be(added.Id);
        detail.DayId.Should().Be(dayId);
        detail.MyFeedback.Should().Be("dislike");
        detail.Dish.Id.Should().Be(publicDish.DishId);
        detail.Dish.Status.Should().Be("hidden");
        detail.Dish.IsAvailable.Should().BeFalse();
        detail.Dish.Images.Should().HaveCount(2);
        detail.Dish.Rank.Should().BeNull();
        detail.Dish.Restaurant.DistanceKm.Should().BeNull();
        detail.Restaurant.Id.Should().Be(publicDish.RestaurantId);
        detail.Restaurant.Images.Should().ContainSingle();
        detail.Restaurant.BusinessHours.Should().ContainSingle();
        detail.CanRate.Should().BeFalse();
        detail.MyReview.Should().NotBeNull();
        detail.MyReview!.Status.Should().Be("hidden");
        detail.MyReview.Images.Should().ContainSingle();
        detail.Restaurant.MyReview!.Id.Should().Be(detail.MyReview.Id);

        // STEP-13: Send GET /api/v1/roadmaps/{roadmap.Overview.Id}/items/999999 and keep missingItem.
        using var missingItem = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/999999");
        // STEP-14: Obtain otherRoadmap through CreateRoadmapAsync; assert missingItem.StatusCode is HttpStatusCode.NotFound and 1 more.
        missingItem.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missingItem.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_ITEM_NOT_FOUND");

        var otherRoadmap = await CreateRoadmapAsync(client, "Other owned trip", null, 1);
        // STEP-15: Send GET /api/v1/roadmaps/{otherRoadmap.Overview.Id}/items/{added.Id} and keep mismatchedRoadmap.
        using var mismatchedRoadmap = await client.GetAsync(
            $"/api/v1/roadmaps/{otherRoadmap.Overview.Id}/items/{added.Id}");
        // STEP-16: Obtain other through RegisterTravelerAndLoginAsync; assert mismatchedRoadmap.StatusCode is HttpStatusCode.NotFound and 1 more.
        mismatchedRoadmap.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await mismatchedRoadmap.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_ITEM_NOT_FOUND");

        var other = await RegisterTravelerAndLoginAsync(client, "roadmap-item-detail-other@integration.test");
        // STEP-17: Authenticate the HTTP client through Authorize.
        Authorize(client, other.AccessToken);
        // STEP-18: Send GET /api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id} and keep foreignItem.
        using var foreignItem = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");
        // STEP-19: Assert foreignItem.StatusCode is HttpStatusCode.NotFound and the response body.ErrorCode is "ROADMAP_ITEM_NOT_FOUND".
        foreignItem.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreignItem.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_ITEM_NOT_FOUND");

        // STEP-20: Authenticate the HTTP client through Authorize.
        Authorize(client, owner.AccessToken);
        // STEP-21: Send DELETE /api/v1/roadmaps/{roadmap.Overview.Id} and keep deleteResponse.
        using var deleteResponse = await client.DeleteAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");
        // STEP-22: Ensure deleteResponse succeeded.
        deleteResponse.EnsureSuccessStatusCode();
        // STEP-23: Send GET /api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id} and keep deletedRoadmapItem.
        using var deletedRoadmapItem = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");
        // STEP-24: Assert deletedRoadmapItem.StatusCode is HttpStatusCode.NotFound and the response body.ErrorCode is "ROADMAP_ITEM_NOT_FOUND".
        deletedRoadmapItem.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await deletedRoadmapItem.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_ITEM_NOT_FOUND");
    }

    // TEST-10: Replace an item atomically, detach its interactions, then update the replacement in place.
    /// <summary>Verifies both RM-08 write paths, new-id semantics, and ON DELETE SET NULL through HTTP and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task UpdateRoadmapItem_ShouldReplaceThenRepositionWithoutLeakingOldInteractions()
    {
        // STEP-01: Reset the test database, get publicDish from DiscoverySeed.PublicDishAsync and get replacementDishId from _factory.ExecuteDbAsync.
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        var replacementDishId = await _factory.ExecuteDbAsync(async context =>
        {
            var dish = new Dish
            {
                RestaurantId = publicDish.RestaurantId,
                Name = "Replacement dish",
                Price = 60_000,
                DishForm = DishForm.Dry,
                IsAvailable = true,
                Status = DishStatus.Active
            };
            context.Dishes.Add(dish);
            await context.SaveChangesAsync();
            return dish.Id;
        });
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain traveler through RegisterTravelerAndLoginAsync.
        var traveler = await RegisterTravelerAndLoginAsync(client, "roadmap-update-item@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, traveler.AccessToken);
        // STEP-05: Obtain roadmap through CreateRoadmapAsync.
        var roadmap = await CreateRoadmapAsync(client, "Editable trip", null, 2);
        // STEP-06: Derive firstDayId from Itinerary.Single(filtered).DayId and derive secondDayId from Itinerary.Single(filtered).DayId.
        var firstDayId = roadmap.Itinerary.Single(day => day.DayNumber == 1).DayId;
        var secondDayId = roadmap.Itinerary.Single(day => day.DayNumber == 2).DayId;
        // STEP-07: Send POST /api/v1/roadmaps/{roadmap.Overview.Id}/days/{firstDayId}/items with a AddRoadmapItemRequestDto body and keep addResponse.
        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{firstDayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "breakfast", SortOrder = 0 });
        // STEP-08: Obtain original through SuccessDataAsync.
        var original = await SuccessDataAsync<RoadmapItemDto>(addResponse);
        // STEP-09: Get interactionIds from _factory.ExecuteDbAsync and create recoRequestId through Guid.NewGuid.
        var interactionIds = await _factory.ExecuteDbAsync(async context =>
        {
            var feedback = new DishFeedback
            {
                EventUuid = Guid.NewGuid(),
                UserId = traveler.User.Id,
                DishId = publicDish.DishId,
                RoadmapItemId = original.Id,
                Value = FeedbackValue.Like
            };
            var review = new RestaurantReview
            {
                RestaurantId = publicDish.RestaurantId,
                UserId = traveler.User.Id,
                Rating = 5,
                Content = "Original item review",
                RoadmapItemId = original.Id
            };
            context.DishFeedbacks.Add(feedback);
            context.RestaurantReviews.Add(review);
            await context.SaveChangesAsync();
            return (FeedbackId: feedback.Id, ReviewId: review.Id);
        });
        var recoRequestId = Guid.NewGuid();

        // STEP-10: Send PATCH /api/v1/roadmaps/{roadmap.Overview.Id}/items/{original.Id} with a UpdateRoadmapItemRequestDto body and keep replaceResponse.
        using var replaceResponse = await client.PatchAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{original.Id}",
            new UpdateRoadmapItemRequestDto
            {
                DishId = replacementDishId,
                RecoRequestId = recoRequestId,
                DayId = secondDayId,
                MealSlot = "late_night",
                SortOrder = short.MaxValue
            });

        // STEP-11: Obtain replacement through SuccessDataAsync; assert replaceResponse.StatusCode is HttpStatusCode.OK.
        replaceResponse.StatusCode.Should().Be(HttpStatusCode.OK, await replaceResponse.Content.ReadAsStringAsync());
        var replacement = await SuccessDataAsync<RoadmapItemDto>(replaceResponse);
        // STEP-12: Assert replacement.Id is not original.Id, replacement.DayId is secondDayId, replacement.DayNumber is 2 and 6 more.
        replacement.Id.Should().NotBe(original.Id);
        replacement.DayId.Should().Be(secondDayId);
        replacement.DayNumber.Should().Be(2);
        replacement.Dish.Id.Should().Be(replacementDishId);
        replacement.MealSlot.Should().Be("late_night");
        replacement.SortOrder.Should().Be(short.MaxValue);
        replacement.Source.Should().Be("manual");
        replacement.RecoRequestId.Should().Be(recoRequestId);
        replacement.MyFeedback.Should().BeNull();
        // STEP-13: Assert in the database context.RoadmapItems.AnyAsync(filtered) is false, persisted.RoadmapDayId is secondDayId and 2 more.
        await _factory.ExecuteDbAsync(async context =>
        {
            (await context.RoadmapItems.AnyAsync(item => item.Id == original.Id)).Should().BeFalse();
            var persisted = await context.RoadmapItems.SingleAsync(item => item.Id == replacement.Id);
            persisted.RoadmapDayId.Should().Be(secondDayId);
            persisted.DishId.Should().Be(replacementDishId);
            (await context.DishFeedbacks.SingleAsync(item => item.Id == interactionIds.FeedbackId))
                .RoadmapItemId.Should().BeNull();
            (await context.RestaurantReviews.SingleAsync(item => item.Id == interactionIds.ReviewId))
                .RoadmapItemId.Should().BeNull();
        });

        // STEP-14: Send PATCH /api/v1/roadmaps/{roadmap.Overview.Id}/items/{replacement.Id} with a UpdateRoadmapItemRequestDto body and keep moveResponse.
        using var moveResponse = await client.PatchAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{replacement.Id}",
            new UpdateRoadmapItemRequestDto
            {
                DayId = firstDayId,
                MealSlot = "lunch",
                SortOrder = short.MinValue
            });

        // STEP-15: Obtain moved through SuccessDataAsync; assert moveResponse.StatusCode is HttpStatusCode.OK.
        moveResponse.StatusCode.Should().Be(HttpStatusCode.OK, await moveResponse.Content.ReadAsStringAsync());
        var moved = await SuccessDataAsync<RoadmapItemDto>(moveResponse);
        // STEP-16: Assert moved.Id is replacement.Id, moved.DayId is firstDayId, moved.MealSlot is "lunch" and moved.SortOrder is short.MinValue.
        moved.Id.Should().Be(replacement.Id);
        moved.DayId.Should().Be(firstDayId);
        moved.MealSlot.Should().Be("lunch");
        moved.SortOrder.Should().Be(short.MinValue);
        // STEP-17: Assert in the database persisted.RoadmapDayId is firstDayId, persisted.MealSlot is MealSlot.Lunch and persisted.SortOrder is short.MinValue.
        await _factory.ExecuteDbAsync(async context =>
        {
            var persisted = await context.RoadmapItems.SingleAsync(item => item.Id == replacement.Id);
            persisted.RoadmapDayId.Should().Be(firstDayId);
            persisted.MealSlot.Should().Be(MealSlot.Lunch);
            persisted.SortOrder.Should().Be(short.MinValue);
        });
    }

    // TEST-11: Reject every invalid RM-08 resource and availability scope at the real HTTP boundary.
    /// <summary>Verifies missing, foreign, cross-roadmap-day, and unavailable replacement errors in PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task UpdateRoadmapItem_WithInvalidTargets_ShouldReturnDocumentedErrors()
    {
        // STEP-01: Reset the test database and get publicDish from DiscoverySeed.PublicDishAsync.
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain owner through RegisterTravelerAndLoginAsync.
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-update-errors-owner@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, owner.AccessToken);
        // STEP-05: Obtain roadmap through CreateRoadmapAsync.
        var roadmap = await CreateRoadmapAsync(client, "Protected editable trip", null, 1);
        // STEP-06: Derive dayId from roadmap.Itinerary.Single().DayId.
        var dayId = roadmap.Itinerary.Single().DayId;
        // STEP-07: Send POST /api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items with a AddRoadmapItemRequestDto body and keep addResponse.
        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        // STEP-08: Obtain item through SuccessDataAsync and otherRoadmap through CreateRoadmapAsync.
        var item = await SuccessDataAsync<RoadmapItemDto>(addResponse);
        var otherRoadmap = await CreateRoadmapAsync(client, "Other trip", null, 1);
        // STEP-09: Derive otherDayId from Itinerary.Single().DayId.
        var otherDayId = otherRoadmap.Itinerary.Single().DayId;

        // STEP-10: Send PATCH /api/v1/roadmaps/{roadmap.Overview.Id}/items/999999 with a UpdateRoadmapItemRequestDto body and keep missingItem.
        using var missingItem = await client.PatchAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/999999",
            new UpdateRoadmapItemRequestDto { MealSlot = "dinner" });
        // STEP-11: Send PATCH /api/v1/roadmaps/{roadmap.Overview.Id}/items/{item.Id} with a UpdateRoadmapItemRequestDto body and keep crossRoadmapDay.
        using var crossRoadmapDay = await client.PatchAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{item.Id}",
            new UpdateRoadmapItemRequestDto { DayId = otherDayId });
        // STEP-12: Send PATCH /api/v1/roadmaps/{roadmap.Overview.Id}/items/{item.Id} with a UpdateRoadmapItemRequestDto body and keep unavailableDish.
        using var unavailableDish = await client.PatchAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{item.Id}",
            new UpdateRoadmapItemRequestDto { DishId = publicDish.HiddenDishId });

        // STEP-13: Obtain other through RegisterTravelerAndLoginAsync; assert for each response, response.StatusCode is HttpStatusCode.NotFound and 3 more.
        foreach (var response in new[] { missingItem, crossRoadmapDay })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
                .Should().Be("ROADMAP_ITEM_NOT_FOUND");
        }
        unavailableDish.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unavailableDish.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("DISH_NOT_AVAILABLE");

        var other = await RegisterTravelerAndLoginAsync(client, "roadmap-update-errors-other@integration.test");
        // STEP-14: Authenticate the HTTP client through Authorize.
        Authorize(client, other.AccessToken);
        // STEP-15: Send PATCH /api/v1/roadmaps/{roadmap.Overview.Id}/items/{item.Id} with a UpdateRoadmapItemRequestDto body and keep foreignItem.
        using var foreignItem = await client.PatchAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{item.Id}",
            new UpdateRoadmapItemRequestDto { MealSlot = "dinner" });
        // STEP-16: Assert foreignItem.StatusCode is HttpStatusCode.NotFound and the response body.ErrorCode is "ROADMAP_ITEM_NOT_FOUND".
        foreignItem.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreignItem.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_ITEM_NOT_FOUND");
    }

    // TEST-12: Hard-delete an owned item while retaining feedback and review history with null item references.
    /// <summary>Verifies RM-09 persistence semantics, response shape, and repeat-delete behavior through PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task DeleteRoadmapItem_ShouldRemoveOnlyItemAndPreserveRelatedHistory()
    {
        // STEP-01: Reset the test database and get publicDish from DiscoverySeed.PublicDishAsync.
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain owner through RegisterTravelerAndLoginAsync.
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-item-delete-owner@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, owner.AccessToken);
        // STEP-05: Obtain roadmap through CreateRoadmapAsync.
        var roadmap = await CreateRoadmapAsync(client, "Delete item trip", null, 1);
        // STEP-06: Derive dayId from roadmap.Itinerary.Single().DayId.
        var dayId = roadmap.Itinerary.Single().DayId;
        // STEP-07: Send POST /api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items with a AddRoadmapItemRequestDto body and keep addResponse.
        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "dinner" });
        // STEP-08: Obtain added through SuccessDataAsync.
        var added = await SuccessDataAsync<RoadmapItemDto>(addResponse);
        // STEP-09: Get relatedIds from _factory.ExecuteDbAsync.
        var relatedIds = await _factory.ExecuteDbAsync(async context =>
        {
            var feedback = new DishFeedback
            {
                EventUuid = Guid.NewGuid(),
                UserId = owner.User.Id,
                DishId = publicDish.DishId,
                RoadmapItemId = added.Id,
                Value = FeedbackValue.Like
            };
            var review = new RestaurantReview
            {
                RestaurantId = publicDish.RestaurantId,
                UserId = owner.User.Id,
                Rating = 5,
                Content = "History must survive item deletion",
                RoadmapItemId = added.Id,
                Status = ContentStatus.Visible
            };
            context.DishFeedbacks.Add(feedback);
            context.RestaurantReviews.Add(review);
            await context.SaveChangesAsync();
            return (FeedbackId: feedback.Id, ReviewId: review.Id);
        });

        // STEP-10: Send DELETE /api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id} and keep response.
        using var response = await client.DeleteAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");

        // STEP-11: Read document from the response; assert response.StatusCode is HttpStatusCode.OK, document.RootElement.success is true and 1 more.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        // STEP-12: Assert in the database context.RoadmapItems.AnyAsync(filtered) is false, context.RoadmapDays.AnyAsync(filtered) is true and 4 more.
        await _factory.ExecuteDbAsync(async context =>
        {
            (await context.RoadmapItems.AnyAsync(item => item.Id == added.Id)).Should().BeFalse();
            (await context.RoadmapDays.AnyAsync(day => day.Id == dayId)).Should().BeTrue();
            (await context.Roadmaps.AnyAsync(value => value.Id == roadmap.Overview.Id)).Should().BeTrue();
            (await context.Dishes.AnyAsync(dish => dish.Id == publicDish.DishId)).Should().BeTrue();
            var feedback = await context.DishFeedbacks.SingleAsync(value => value.Id == relatedIds.FeedbackId);
            feedback.RoadmapItemId.Should().BeNull();
            var review = await context.RestaurantReviews.SingleAsync(value => value.Id == relatedIds.ReviewId);
            review.RoadmapItemId.Should().BeNull();
        });

        // STEP-13: Send DELETE /api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id} and keep repeated.
        using var repeated = await client.DeleteAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");
        // STEP-14: Assert repeated.StatusCode is HttpStatusCode.NotFound and the response body.ErrorCode is "ROADMAP_ITEM_NOT_FOUND".
        repeated.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await repeated.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_ITEM_NOT_FOUND");
    }

    // TEST-13: Conceal missing, mismatched, foreign, and soft-deleted-parent items without deleting them.
    /// <summary>Verifies every RM-09 ownership and membership boundary through HTTP and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task DeleteRoadmapItem_WithInvalidScope_ShouldReturnItemNotFoundWithoutMutation()
    {
        // STEP-01: Reset the test database and get publicDish from DiscoverySeed.PublicDishAsync.
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain owner through RegisterTravelerAndLoginAsync.
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-item-delete-scope-owner@integration.test");
        // STEP-04: Authenticate the HTTP client through Authorize.
        Authorize(client, owner.AccessToken);
        // STEP-05: Obtain roadmap through CreateRoadmapAsync and otherRoadmap through CreateRoadmapAsync.
        var roadmap = await CreateRoadmapAsync(client, "Protected item trip", null, 1);
        var otherRoadmap = await CreateRoadmapAsync(client, "Other matching owner trip", null, 1);
        // STEP-06: Derive dayId from roadmap.Itinerary.Single().DayId.
        var dayId = roadmap.Itinerary.Single().DayId;
        // STEP-07: Send POST /api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items with a AddRoadmapItemRequestDto body and keep addResponse.
        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        // STEP-08: Obtain added through SuccessDataAsync.
        var added = await SuccessDataAsync<RoadmapItemDto>(addResponse);

        // STEP-09: Send DELETE /api/v1/roadmaps/{roadmap.Overview.Id}/items/999999 and keep missing.
        using var missing = await client.DeleteAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/999999");
        // STEP-10: Send DELETE /api/v1/roadmaps/{otherRoadmap.Overview.Id}/items/{added.Id} and keep mismatched.
        using var mismatched = await client.DeleteAsync(
            $"/api/v1/roadmaps/{otherRoadmap.Overview.Id}/items/{added.Id}");
        // STEP-11: Obtain other through RegisterTravelerAndLoginAsync.
        var other = await RegisterTravelerAndLoginAsync(client, "roadmap-item-delete-scope-other@integration.test");
        // STEP-12: Authenticate the HTTP client through Authorize.
        Authorize(client, other.AccessToken);
        // STEP-13: Send DELETE /api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id} and keep foreign.
        using var foreign = await client.DeleteAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");

        // STEP-14: Authenticate the HTTP client through Authorize.
        Authorize(client, owner.AccessToken);
        // STEP-15: Send DELETE /api/v1/roadmaps/{roadmap.Overview.Id} and keep deleteRoadmap.
        using var deleteRoadmap = await client.DeleteAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");
        // STEP-16: Ensure deleteRoadmap succeeded.
        deleteRoadmap.EnsureSuccessStatusCode();
        // STEP-17: Send DELETE /api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id} and keep deletedParent.
        using var deletedParent = await client.DeleteAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");

        // STEP-18: Assert for each response, response.StatusCode is HttpStatusCode.NotFound and the response body.ErrorCode is "ROADMAP_ITEM_NOT_FOUND".
        foreach (var response in new[] { missing, mismatched, foreign, deletedParent })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
                .Should().Be("ROADMAP_ITEM_NOT_FOUND");
        }

        // STEP-19: Assert in the database context.RoadmapItems.AnyAsync(filtered) is true.
        await _factory.ExecuteDbAsync(async context =>
            (await context.RoadmapItems.AnyAsync(item => item.Id == added.Id)).Should().BeTrue());
    }

    // TEST-14: Rate an item once: dish_feedbacks keeps the item's reco request id, the dish counters move, the
    // outbox carries FEEDBACK_CREATED for RECO-04, and RM-12 shows the traveler's feedback.
    /// <summary>Verifies the RM-13 transaction through HTTP and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task RateRoadmapItem_ShouldStoreFeedbackCountersAndOutboxEventOnce()
    {
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-rate-owner@integration.test");
        Authorize(client, owner.AccessToken);
        var roadmap = await CreateRoadmapAsync(client, "Rate trip", null, 1);
        var dayId = roadmap.Itinerary.Single().DayId;
        var recoRequestId = Guid.NewGuid();
        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch", RecoRequestId = recoRequestId });
        var item = await SuccessDataAsync<RoadmapItemDto>(addResponse);

        using var rated = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{item.Id}/feedback", new { value = "like" });

        rated.StatusCode.Should().Be(HttpStatusCode.Created, await rated.Content.ReadAsStringAsync());
        var feedback = await SuccessDataAsync<RateRoadmapItemResponseDto>(rated);
        feedback.Should().BeEquivalentTo(new { Value = "like", DishId = publicDish.DishId, LikeCount = 1, DislikeCount = 0 });
        feedback.FeedbackId.Should().BePositive();
        feedback.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        await _factory.ExecuteDbAsync(async context =>
        {
            var row = await context.DishFeedbacks.SingleAsync(value => value.Id == feedback.FeedbackId);
            row.Should().BeEquivalentTo(new
            {
                UserId = owner.User.Id,
                DishId = publicDish.DishId,
                Value = FeedbackValue.Like,
                RoadmapItemId = (long?)item.Id,
                RecoRequestId = (Guid?)recoRequestId
            });
            var outbox = await context.OutboxMessages.SingleAsync(message => message.EventUuid == row.EventUuid);
            outbox.EventType.Should().Be(OutboxEventType.FeedbackCreated);
            outbox.AggregateKey.Should().Be($"user:{owner.User.Id}");
            using var payload = JsonDocument.Parse(outbox.Payload);
            payload.RootElement.GetProperty("event_type").GetString().Should().Be("like");
            payload.RootElement.GetProperty("dish_id").GetInt32().Should().Be(publicDish.DishId);
            payload.RootElement.GetProperty("request_id").GetGuid().Should().Be(recoRequestId);
        });

        using var detail = await client.GetAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{item.Id}");
        (await SuccessDataAsync<RoadmapItemDetailDto>(detail)).MyFeedback.Should().Be("like");

        using var again = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{item.Id}/feedback", new { value = "dislike" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("FEEDBACK_ALREADY_EXISTS");
        await _factory.ExecuteDbAsync(async context =>
        {
            var dish = await context.Dishes.AsNoTracking().SingleAsync(value => value.Id == publicDish.DishId);
            (dish.LikeCount, dish.DislikeCount).Should().Be((1, 0));
            (await context.DishFeedbacks.CountAsync()).Should().Be(1);
        });
    }

    // TEST-15: Conceal foreign or mismatched items, and still accept feedback after the dish was hidden.
    /// <summary>Verifies RM-13 ownership boundaries and the decision that a hidden dish can still be rated.</summary>
    [PostgresEndToEndFact]
    public async Task RateRoadmapItem_ShouldHideForeignItemsAndAcceptHiddenDishes()
    {
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-rate-scope-owner@integration.test");
        Authorize(client, owner.AccessToken);
        var roadmap = await CreateRoadmapAsync(client, "Scoped rate trip", null, 1);
        var otherRoadmap = await CreateRoadmapAsync(client, "Other trip", null, 1);
        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{roadmap.Itinerary.Single().DayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "dinner" });
        var item = await SuccessDataAsync<RoadmapItemDto>(addResponse);

        using var mismatched = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{otherRoadmap.Overview.Id}/items/{item.Id}/feedback", new { value = "like" });
        mismatched.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await mismatched.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("ROADMAP_ITEM_NOT_FOUND");

        using var stranger = _factory.CreateClient();
        var other = await RegisterTravelerAndLoginAsync(stranger, "roadmap-rate-stranger@integration.test");
        Authorize(stranger, other.AccessToken);
        using var foreign = await stranger.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{item.Id}/feedback", new { value = "like" });
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await _factory.ExecuteDbAsync(async context =>
        {
            var dish = await context.Dishes.SingleAsync(value => value.Id == publicDish.DishId);
            dish.Status = DishStatus.Hidden;
            await context.SaveChangesAsync();
        });
        using var hidden = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{item.Id}/feedback", new { value = "dislike" });

        hidden.StatusCode.Should().Be(HttpStatusCode.Created, await hidden.Content.ReadAsStringAsync());
        (await SuccessDataAsync<RateRoadmapItemResponseDto>(hidden)).Should().BeEquivalentTo(new { Value = "dislike", LikeCount = 0, DislikeCount = 1 });
    }

    /// <summary>Creates a valid image media row for PostgreSQL relationship coverage.</summary>
    private static MediaFile TestMedia(int uploaderId, string name) => new()
    {
        UploaderId = uploaderId,
        StorageKey = $"image/upload/roadmap/{name}-{Guid.NewGuid():N}.jpg",
        Url = $"https://cdn.test/{name}.jpg",
        MimeType = "image/jpeg"
    };

    /// <summary>Registers, verifies, and logs in a traveler through public HTTP endpoints.</summary>
    private async Task<AuthResultDto> RegisterTravelerAndLoginAsync(HttpClient client, string email)
    {
        client.DefaultRequestHeaders.Authorization = null;
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterAccountRequestDto
        {
            Email = email,
            Password = Password,
            DisplayName = "Integration Traveler",
            Role = "TRAVELER"
        });
        register.EnsureSuccessStatusCode();
        using var verify = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequestDto
        {
            Token = _factory.Email.VerificationToken(email)
        });
        verify.EnsureSuccessStatusCode();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequestDto
        {
            Email = email,
            Password = Password
        });
        login.EnsureSuccessStatusCode();
        return await SuccessDataAsync<AuthResultDto>(login);
    }

    /// <summary>Creates one roadmap through RM-01 and returns its complete response.</summary>
    private static async Task<RoadmapDetailDto> CreateRoadmapAsync(
        HttpClient client,
        string title,
        DateOnly? startDate,
        short numDays)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/roadmaps", new CreateRoadmapRequestDto
        {
            Title = title,
            StartDate = startDate,
            NumDays = numDays
        });
        response.EnsureSuccessStatusCode();
        return await SuccessDataAsync<RoadmapDetailDto>(response);
    }

    /// <summary>Adds the issued bearer token to subsequent traveler requests.</summary>
    private static void Authorize(HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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
