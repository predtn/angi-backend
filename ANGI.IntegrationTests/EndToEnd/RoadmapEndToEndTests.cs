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
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterTravelerAndLoginAsync(client, "roadmap-owner@integration.test");
        Authorize(client, auth.AccessToken);

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

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());
        var created = await SuccessDataAsync<RoadmapDetailDto>(createResponse);
        created.Itinerary.Select(day => day.DayNumber).Should().Equal(1, 2, 3);

        using var updateResponse = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{created.Overview.Id}", new
        {
            title = "Updated integration trip",
            numDays = 2,
            originMode = "gps"
        });

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK, await updateResponse.Content.ReadAsStringAsync());
        var updated = await SuccessDataAsync<RoadmapDetailDto>(updateResponse);
        updated.Overview.Title.Should().Be("Updated integration trip");
        updated.Overview.OriginMode.Should().Be("gps");
        updated.Overview.OriginLatitude.Should().BeNull();
        updated.Itinerary.Select(day => day.DayNumber).Should().Equal(1, 2);
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
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var first = await RegisterTravelerAndLoginAsync(client, "first-roadmap@integration.test");
        Authorize(client, first.AccessToken);
        using var createResponse = await client.PostAsJsonAsync("/api/v1/roadmaps", new CreateRoadmapRequestDto
        {
            Title = "Private trip",
            NumDays = 1
        });
        var created = await SuccessDataAsync<RoadmapDetailDto>(createResponse);
        var second = await RegisterTravelerAndLoginAsync(client, "second-roadmap@integration.test");
        Authorize(client, second.AccessToken);

        using var updateResponse = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{created.Overview.Id}", new
        {
            title = "Unauthorized change"
        });
        using var detailResponse = await client.GetAsync($"/api/v1/roadmaps/{created.Overview.Id}");
        using var routeResponse = await client.GetAsync(
            $"/api/v1/roadmaps/{created.Overview.Id}/days/{created.Itinerary.Single().DayId}/route");
        using var deleteResponse = await client.DeleteAsync($"/api/v1/roadmaps/{created.Overview.Id}");

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
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-list-owner@integration.test");
        Authorize(client, owner.AccessToken);
        var oldest = await CreateRoadmapAsync(client, "Oldest trip", new DateOnly(2026, 10, 10), 3);
        var deleted = await CreateRoadmapAsync(client, "Deleted trip", null, 1);
        var newest = await CreateRoadmapAsync(client, "Newest trip", null, 1);

        var anotherTraveler = await RegisterTravelerAndLoginAsync(client, "roadmap-list-other@integration.test");
        Authorize(client, anotherTraveler.AccessToken);
        await CreateRoadmapAsync(client, "Another traveler's trip", null, 1);

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

        using var firstPageResponse = await client.GetAsync("/api/v1/roadmaps?page=1&pageSize=1");
        firstPageResponse.StatusCode.Should().Be(HttpStatusCode.OK, await firstPageResponse.Content.ReadAsStringAsync());
        var firstPage = await SuccessDataAsync<Paged<RoadmapSummaryDto>>(firstPageResponse);
        firstPage.Items.Should().ContainSingle().Which.Id.Should().Be(newest.Overview.Id);
        firstPage.TotalItems.Should().Be(2);
        firstPage.TotalPages.Should().Be(2);

        using var secondPageResponse = await client.GetAsync("/api/v1/roadmaps?page=2&pageSize=1");
        var secondPage = await SuccessDataAsync<Paged<RoadmapSummaryDto>>(secondPageResponse);
        var summary = secondPage.Items.Should().ContainSingle().Subject;
        summary.Id.Should().Be(oldest.Overview.Id);
        summary.EndDate.Should().Be(new DateOnly(2026, 10, 12));
        summary.ItemCount.Should().Be(1);
        summary.TotalEstimatedCost.Should().Be(45_000);
        summary.CoverUrl.Should().Be("https://cdn.test/dishes/cover.jpg");

        using var emptyPageResponse = await client.GetAsync("/api/v1/roadmaps?page=3&pageSize=1");
        var emptyPage = await SuccessDataAsync<Paged<RoadmapSummaryDto>>(emptyPageResponse);
        emptyPage.Items.Should().BeEmpty();
        emptyPage.TotalItems.Should().Be(2);

        using var invalidResponse = await client.GetAsync("/api/v1/roadmaps?page=0&pageSize=101");
        invalidResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var invalid = await invalidResponse.Content.ReadFromJsonAsync<ApiResponse<object>>();
        invalid!.ErrorCode.Should().Be("VALIDATION_FAILED");
    }

    // TEST-04: Read current dish data, item feedback, itinerary ordering, and calculated budget through RM-03.
    /// <summary>Verifies the real RM-03 repository graph and mapper against PostgreSQL through the HTTP pipeline.</summary>
    [PostgresEndToEndFact]
    public async Task GetRoadmapDetail_ShouldReturnOwnedCurrentCalculatedAggregate()
    {
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var traveler = await RegisterTravelerAndLoginAsync(client, "roadmap-detail@integration.test");
        Authorize(client, traveler.AccessToken);
        var roadmap = await CreateRoadmapAsync(client, "Detail trip", new DateOnly(2026, 10, 20), 2);
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

        using var response = await client.GetAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var detail = await SuccessDataAsync<RoadmapDetailDto>(response);
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
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var traveler = await RegisterTravelerAndLoginAsync(client, "roadmap-route@integration.test");
        Authorize(client, traveler.AccessToken);
        using var createResponse = await client.PostAsJsonAsync("/api/v1/roadmaps", new CreateRoadmapRequestDto
        {
            Title = "Route trip",
            NumDays = 1,
            OriginMode = "pinned",
            OriginLatitude = 11m,
            OriginLongitude = 107m,
            OriginLabel = "Pinned point"
        });
        var roadmap = await SuccessDataAsync<RoadmapDetailDto>(createResponse);
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

        using var response = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{itemIds.DayId}/route" +
            "?originLat=10.775&originLng=106.700&profile=walking");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var route = await SuccessDataAsync<GetRoadmapDayRouteResponseDto>(response);
        route.Profile.Should().Be("walking");
        route.Origin.Should().NotBeNull();
        route.Origin!.Source.Should().Be("gps");
        route.OriginSkipped.Should().BeFalse();
        route.Legs.Select(leg => (leg.FromItemId, leg.ToItemId)).Should().Equal(
            ((long?)null, itemIds.FirstItemId), ((long?)itemIds.FirstItemId, itemIds.SecondItemId));
        route.IsEstimated.Should().BeTrue();
        route.TotalDistanceMeters.Should().Be(0);
        route.TotalDurationSeconds.Should().Be(0);

        using var missingDay = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/999999/route");
        missingDay.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await missingDay.Content.ReadFromJsonAsync<ApiResponse<object>>();
        error!.ErrorCode.Should().Be("DAY_NOT_FOUND");
    }

    // TEST-06: Soft-delete only the owned roadmap while preserving its child records.
    /// <summary>Verifies RM-05 persistence, response, visibility, and repeated-delete behavior end to end.</summary>
    [PostgresEndToEndFact]
    public async Task DeleteRoadmap_ShouldSoftDeleteAndHideOnlyTheOwnedRoadmap()
    {
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var traveler = await RegisterTravelerAndLoginAsync(client, "roadmap-delete@integration.test");
        Authorize(client, traveler.AccessToken);
        var roadmap = await CreateRoadmapAsync(client, "Delete trip", null, 1);
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

        using var deleteResponse = await client.DeleteAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK, await deleteResponse.Content.ReadAsStringAsync());
        var envelope = await deleteResponse.Content.ReadFromJsonAsync<ApiResponse<object>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().BeNull();
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

        using var detailResponse = await client.GetAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");
        using var repeatedDeleteResponse = await client.DeleteAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");
        foreach (var response in new[] { detailResponse, repeatedDeleteResponse })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            error!.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        }

        using var listResponse = await client.GetAsync("/api/v1/roadmaps");
        var list = await SuccessDataAsync<Paged<RoadmapSummaryDto>>(listResponse);
        list.Items.Should().NotContain(item => item.Id == roadmap.Overview.Id);
    }

    // TEST-07: Suggest an eligible dish, add it at the end of a meal, then exclude it from later suggestions.
    /// <summary>Verifies RM-06 and RM-07 together through HTTP, real repositories, PostgreSQL, and reco fallback.</summary>
    [PostgresEndToEndFact]
    public async Task SuggestAndAddRoadmapItem_ShouldPersistAndExcludeExistingDish()
    {
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var traveler = await RegisterTravelerAndLoginAsync(client, "roadmap-add-item@integration.test");
        Authorize(client, traveler.AccessToken);
        using var createResponse = await client.PostAsJsonAsync("/api/v1/roadmaps", new CreateRoadmapRequestDto
        {
            Title = "Manual food trip",
            NumDays = 1,
            OriginMode = "pinned",
            OriginLatitude = 10.775m,
            OriginLongitude = 106.700m,
            OriginLabel = "Hotel"
        });
        var roadmap = await SuccessDataAsync<RoadmapDetailDto>(createResponse);
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

        using var suggestionsResponse = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/suggestions?province=Ho%20Chi%20Minh%20City&mealSlot=lunch&limit=1");

        suggestionsResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await suggestionsResponse.Content.ReadAsStringAsync());
        var suggestions = await SuccessDataAsync<GetRoadmapSuggestionsResponseDto>(suggestionsResponse);
        suggestions.RequestId.Should().BeNull();
        suggestions.Items.Should().ContainSingle().Which.Id.Should().Be(publicDish.DishId);
        suggestions.Items.Single().Rank.Should().Be(1);
        suggestions.NextCursor.Should().BeNull();

        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });

        addResponse.StatusCode.Should().Be(HttpStatusCode.Created, await addResponse.Content.ReadAsStringAsync());
        var added = await SuccessDataAsync<RoadmapItemDto>(addResponse);
        added.DayId.Should().Be(dayId);
        added.DayNumber.Should().Be(1);
        added.SortOrder.Should().Be(5);
        added.Source.Should().Be("manual");
        added.Dish.Id.Should().Be(publicDish.DishId);
        added.Dish.Status.Should().Be("active");
        added.Dish.Tags.Should().ContainSingle(item => item.Code == "lunch");
        added.MyFeedback.Should().BeNull();
        await _factory.ExecuteDbAsync(async context =>
        {
            var persisted = await context.RoadmapItems.SingleAsync(item => item.Id == added.Id);
            persisted.RoadmapDayId.Should().Be(dayId);
            persisted.DishId.Should().Be(publicDish.DishId);
            persisted.MealSlot.Should().Be(MealSlot.Lunch);
            persisted.SortOrder.Should().Be(5);
            persisted.Source.Should().Be(RoadmapItemSource.Manual);
        });

        using var repeatedResponse = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/suggestions?province=Ho%20Chi%20Minh%20City&mealSlot=lunch");
        var repeated = await SuccessDataAsync<GetRoadmapSuggestionsResponseDto>(repeatedResponse);
        repeated.Items.Should().BeEmpty();
    }

    // TEST-08: Enforce ownership, day membership, and dish availability at the real RM-07 boundary.
    /// <summary>Verifies every documented RM-07 business error through HTTP and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task AddRoadmapItem_WithInvalidTargets_ShouldReturnDocumentedErrors()
    {
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-item-errors-owner@integration.test");
        Authorize(client, owner.AccessToken);
        var roadmap = await CreateRoadmapAsync(client, "Protected trip", null, 1);
        var dayId = roadmap.Itinerary.Single().DayId;

        using var missingDay = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/999999/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        missingDay.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missingDay.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("DAY_NOT_FOUND");

        using var hiddenDish = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.HiddenDishId, MealSlot = "lunch" });
        hiddenDish.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await hiddenDish.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("DISH_NOT_AVAILABLE");

        await _factory.ExecuteDbAsync(async context =>
        {
            var dish = await context.Dishes.SingleAsync(item => item.Id == publicDish.DishId);
            dish.IsAvailable = false;
            await context.SaveChangesAsync();
        });
        using var soldOutDish = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        soldOutDish.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await soldOutDish.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("DISH_NOT_AVAILABLE");

        await _factory.ExecuteDbAsync(async context =>
        {
            var dish = await context.Dishes
                .Include(item => item.Restaurant)
                .SingleAsync(item => item.Id == publicDish.DishId);
            dish.IsAvailable = true;
            dish.Restaurant.OperatingStatus = RestaurantOperatingStatus.TemporarilyClosed;
            await context.SaveChangesAsync();
        });
        using var closedRestaurant = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        closedRestaurant.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await closedRestaurant.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("DISH_NOT_AVAILABLE");

        var other = await RegisterTravelerAndLoginAsync(client, "roadmap-item-errors-other@integration.test");
        Authorize(client, other.AccessToken);
        using var foreignRoadmap = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        foreignRoadmap.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreignRoadmap.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_NOT_FOUND");
    }

    // TEST-09: Read full RM-12 detail and conceal every invalid ownership or membership boundary.
    /// <summary>Verifies item, inactive dish, restaurant, feedback, moderated own review, and all 404 scopes in PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task GetRoadmapItemDetail_ShouldReturnCompleteOwnedDataAndHideInvalidTargets()
    {
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-item-detail-owner@integration.test");
        Authorize(client, owner.AccessToken);
        var roadmap = await CreateRoadmapAsync(client, "Item detail trip", null, 1);
        var dayId = roadmap.Itinerary.Single().DayId;
        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        var added = await SuccessDataAsync<RoadmapItemDto>(addResponse);

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

        using var detailResponse = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");

        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK, await detailResponse.Content.ReadAsStringAsync());
        var detail = await SuccessDataAsync<RoadmapItemDetailDto>(detailResponse);
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

        using var missingItem = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/999999");
        missingItem.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missingItem.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_ITEM_NOT_FOUND");

        var otherRoadmap = await CreateRoadmapAsync(client, "Other owned trip", null, 1);
        using var mismatchedRoadmap = await client.GetAsync(
            $"/api/v1/roadmaps/{otherRoadmap.Overview.Id}/items/{added.Id}");
        mismatchedRoadmap.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await mismatchedRoadmap.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_ITEM_NOT_FOUND");

        var other = await RegisterTravelerAndLoginAsync(client, "roadmap-item-detail-other@integration.test");
        Authorize(client, other.AccessToken);
        using var foreignItem = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");
        foreignItem.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreignItem.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_ITEM_NOT_FOUND");

        Authorize(client, owner.AccessToken);
        using var deleteResponse = await client.DeleteAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");
        deleteResponse.EnsureSuccessStatusCode();
        using var deletedRoadmapItem = await client.GetAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");
        deletedRoadmapItem.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await deletedRoadmapItem.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_ITEM_NOT_FOUND");
    }

    // TEST-10: Hard-delete an owned item while retaining feedback and review history with null item references.
    /// <summary>Verifies RM-09 persistence semantics, response shape, and repeat-delete behavior through PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task DeleteRoadmapItem_ShouldRemoveOnlyItemAndPreserveRelatedHistory()
    {
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-item-delete-owner@integration.test");
        Authorize(client, owner.AccessToken);
        var roadmap = await CreateRoadmapAsync(client, "Delete item trip", null, 1);
        var dayId = roadmap.Itinerary.Single().DayId;
        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "dinner" });
        var added = await SuccessDataAsync<RoadmapItemDto>(addResponse);
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

        using var response = await client.DeleteAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
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

        using var repeated = await client.DeleteAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");
        repeated.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await repeated.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
            .Should().Be("ROADMAP_ITEM_NOT_FOUND");
    }

    // TEST-11: Conceal missing, mismatched, foreign, and soft-deleted-parent items without deleting them.
    /// <summary>Verifies every RM-09 ownership and membership boundary through HTTP and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task DeleteRoadmapItem_WithInvalidScope_ShouldReturnItemNotFoundWithoutMutation()
    {
        await _factory.ResetDatabaseAsync();
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var owner = await RegisterTravelerAndLoginAsync(client, "roadmap-item-delete-scope-owner@integration.test");
        Authorize(client, owner.AccessToken);
        var roadmap = await CreateRoadmapAsync(client, "Protected item trip", null, 1);
        var otherRoadmap = await CreateRoadmapAsync(client, "Other matching owner trip", null, 1);
        var dayId = roadmap.Itinerary.Single().DayId;
        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = publicDish.DishId, MealSlot = "lunch" });
        var added = await SuccessDataAsync<RoadmapItemDto>(addResponse);

        using var missing = await client.DeleteAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/999999");
        using var mismatched = await client.DeleteAsync(
            $"/api/v1/roadmaps/{otherRoadmap.Overview.Id}/items/{added.Id}");
        var other = await RegisterTravelerAndLoginAsync(client, "roadmap-item-delete-scope-other@integration.test");
        Authorize(client, other.AccessToken);
        using var foreign = await client.DeleteAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");

        Authorize(client, owner.AccessToken);
        using var deleteRoadmap = await client.DeleteAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}");
        deleteRoadmap.EnsureSuccessStatusCode();
        using var deletedParent = await client.DeleteAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/items/{added.Id}");

        foreach (var response in new[] { missing, mismatched, foreign, deletedParent })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode
                .Should().Be("ROADMAP_ITEM_NOT_FOUND");
        }

        await _factory.ExecuteDbAsync(async context =>
            (await context.RoadmapItems.AnyAsync(item => item.Id == added.Id)).Should().BeTrue());
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
