using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Roadmap;
using ANGI.WebApi.Controllers.Roadmap;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.IntegrationTests.WebApi.Controllers.Roadmap;

public sealed class RoadmapHttpContractTests : IClassFixture<RoadmapApiFactory>
{
    private readonly RoadmapApiFactory _factory;

    /// <summary>Initializes HTTP contract tests with the shared roadmap API host.</summary>
    public RoadmapHttpContractTests(RoadmapApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Return the canonical 401 envelope for every implemented roadmap endpoint without a token.
    /// <summary>Verifies every published roadmap operation, including RM-08 and RM-09, requires authentication.</summary>
    [Theory]
    [InlineData("POST", "/api/v1/roadmaps")]
    [InlineData("GET", "/api/v1/roadmaps")]
    [InlineData("GET", "/api/v1/roadmaps/88")]
    [InlineData("GET", "/api/v1/roadmaps/88/items/1502")]
    [InlineData("GET", "/api/v1/roadmaps/88/days/301/route")]
    [InlineData("GET", "/api/v1/roadmaps/88/suggestions?province=Hue")]
    [InlineData("POST", "/api/v1/roadmaps/88/days/301/items")]
    [InlineData("PATCH", "/api/v1/roadmaps/88/items/1502")]
    [InlineData("PATCH", "/api/v1/roadmaps/88")]
    [InlineData("DELETE", "/api/v1/roadmaps/88")]
    [InlineData("DELETE", "/api/v1/roadmaps/88/items/1502")]
    public async Task Endpoint_WithoutToken_ShouldReturnUnauthorized(string method, string path)
    {
        using var request = JsonRequest(method, path, "{}");

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertErrorCode(response, "UNAUTHORIZED");
    }

    // TEST-02: Return the canonical 403 envelope when a non-traveler calls a roadmap endpoint.
    /// <summary>Verifies the controller enforces the documented TRAVELER role.</summary>
    [Theory]
    [InlineData("POST", "/api/v1/roadmaps")]
    [InlineData("GET", "/api/v1/roadmaps")]
    [InlineData("GET", "/api/v1/roadmaps/88")]
    [InlineData("GET", "/api/v1/roadmaps/88/items/1502")]
    [InlineData("GET", "/api/v1/roadmaps/88/days/301/route")]
    [InlineData("GET", "/api/v1/roadmaps/88/suggestions?province=Hue")]
    [InlineData("POST", "/api/v1/roadmaps/88/days/301/items")]
    [InlineData("PATCH", "/api/v1/roadmaps/88/items/1502")]
    [InlineData("PATCH", "/api/v1/roadmaps/88")]
    [InlineData("DELETE", "/api/v1/roadmaps/88")]
    [InlineData("DELETE", "/api/v1/roadmaps/88/items/1502")]
    public async Task Endpoint_WithWrongRole_ShouldReturnForbidden(string method, string path)
    {
        using var request = JsonRequest(method, path, "{}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("RESTAURANT_OWNER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertErrorCode(response, "FORBIDDEN");
    }

    // TEST-03: Return VALIDATION_FAILED for missing JSON bodies on every roadmap body endpoint.
    /// <summary>Verifies the shared null-body filter covers RM-01, RM-04, RM-07, and RM-08.</summary>
    [Theory]
    [InlineData("POST", "/api/v1/roadmaps")]
    [InlineData("PATCH", "/api/v1/roadmaps/88")]
    [InlineData("POST", "/api/v1/roadmaps/88/days/301/items")]
    [InlineData("PATCH", "/api/v1/roadmaps/88/items/1502")]
    public async Task Endpoint_WithMissingBody_ShouldReturnValidationEnvelope(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertErrorCode(response, "VALIDATION_FAILED");
    }

    // TEST-04: Serialize the complete RM-01 success envelope with HTTP 201.
    /// <summary>Verifies controller routing, request binding, use-case delegation, and JSON shape.</summary>
    [Fact]
    public async Task Create_WithValidBody_ShouldSerializeCreatedRoadmapDetail()
    {
        using var request = JsonRequest("POST", "/api/v1/roadmaps", """
            {"title":"Trip","numDays":3,"originMode":"none"}
            """);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("overview").GetProperty("id").GetInt64().Should().Be(88);
        data.GetProperty("itinerary").GetArrayLength().Should().Be(3);
        data.GetProperty("budget").GetProperty("totalEstimatedCost").GetDecimal().Should().Be(0);
    }

    // TEST-05: Preserve explicit null versus omission while binding the RM-04 request.
    /// <summary>Verifies PATCH model binding can explicitly clear nullable roadmap fields.</summary>
    [Fact]
    public async Task Update_WithExplicitNulls_ShouldSerializeUpdatedRoadmapDetail()
    {
        using var request = JsonRequest("PATCH", "/api/v1/roadmaps/88", """
            {"startDate":null,"budgetAmount":null,"originLabel":null}
            """);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.Update.LastRequest.Should().NotBeNull();
        _factory.Update.LastRequest!.StartDateSpecified.Should().BeTrue();
        _factory.Update.LastRequest.BudgetAmountSpecified.Should().BeTrue();
        _factory.Update.LastRequest.OriginLabelSpecified.Should().BeTrue();
    }

    // TEST-06: Bind RM-02 query parameters and serialize the complete paged response.
    /// <summary>Verifies routing, query binding, use-case delegation, and JSON shape for the roadmap list.</summary>
    [Fact]
    public async Task GetMyRoadmaps_WithValidQuery_ShouldSerializePagedRoadmapSummaries()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/roadmaps?page=2&pageSize=10");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("page").GetInt32().Should().Be(2);
        data.GetProperty("pageSize").GetInt32().Should().Be(10);
        data.GetProperty("totalItems").GetInt64().Should().Be(11);
        data.GetProperty("totalPages").GetInt32().Should().Be(2);
        data.GetProperty("items")[0].GetProperty("originMode").GetString().Should().Be("pinned");
        _factory.GetMyRoadmaps.LastRequest.Should().BeEquivalentTo(new { Page = 2, PageSize = 10 });
    }

    // TEST-07: Serialize the complete RM-03 success envelope through its route.
    /// <summary>Verifies RM-03 routing, use-case delegation, and nested detail JSON shape.</summary>
    [Fact]
    public async Task GetDetail_WithOwnedRoadmap_ShouldSerializeCompleteDetail()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/roadmaps/88");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("overview").GetProperty("id").GetInt64().Should().Be(88);
        data.GetProperty("itinerary").GetArrayLength().Should().Be(1);
        data.GetProperty("budget").GetProperty("totalEstimatedCost").GetDecimal().Should().Be(0);
        _factory.GetDetail.LastRoadmapId.Should().Be(88);
    }

    // TEST-08: Bind every RM-14 query parameter and serialize route metrics.
    /// <summary>Verifies route values, query binding, defaults, and the complete RM-14 response JSON shape.</summary>
    [Fact]
    public async Task GetDayRoute_WithGpsAndProfile_ShouldSerializeRouteResponse()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/roadmaps/88/days/301/route?originLat=16.061204&originLng=108.227531&profile=walking");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("dayId").GetInt64().Should().Be(301);
        data.GetProperty("profile").GetString().Should().Be("walking");
        data.GetProperty("origin").GetProperty("source").GetString().Should().Be("gps");
        data.GetProperty("legs")[0].GetProperty("fromItemId").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("totalDistanceMeters").GetInt32().Should().Be(2350);
        _factory.GetDayRoute.LastCall.Should().BeEquivalentTo(new
        {
            RoadmapId = 88L,
            DayId = 301L,
            OriginLat = (decimal?)16.061204m,
            OriginLng = (decimal?)108.227531m,
            Profile = "walking"
        });
    }

    // TEST-09: Return VALIDATION_FAILED when only one RM-14 GPS coordinate is supplied.
    /// <summary>Verifies the HTTP error envelope and camel-case validation key for the coordinate-pair rule.</summary>
    [Fact]
    public async Task GetDayRoute_WithIncompleteGps_ShouldReturnValidationEnvelope()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/roadmaps/88/days/301/route?originLat=16.061204");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("errorCode").GetString().Should().Be("VALIDATION_FAILED");
        document.RootElement.GetProperty("errors").TryGetProperty("originLng", out _).Should().BeTrue();
    }

    // TEST-10: Serialize the RM-05 success envelope with explicit null data.
    /// <summary>Verifies DELETE routing, route-value delegation, and the documented response JSON shape.</summary>
    [Fact]
    public async Task Delete_WithOwnedRoadmap_ShouldSerializeNullDataSuccessEnvelope()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/roadmaps/88");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("message").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("errorCode").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        _factory.Delete.LastRoadmapId.Should().Be(88);
    }

    // TEST-11: Preserve ROADMAP_NOT_FOUND through the RM-05 exception middleware boundary.
    /// <summary>Verifies a missing, foreign, or already-deleted roadmap uses the documented 404 envelope.</summary>
    [Fact]
    public async Task Delete_WithoutOwnedVisibleRoadmap_ShouldReturnNotFoundEnvelope()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/roadmaps/404");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertErrorCode(response, "ROADMAP_NOT_FOUND");
    }

    // TEST-12: Bind every RM-06 query parameter and serialize DishCard results.
    /// <summary>Verifies RM-06 route/query binding, nullable fields, and complete success JSON shape.</summary>
    [Fact]
    public async Task GetSuggestions_WithAllFilters_ShouldSerializeRankedDishCards()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/roadmaps/88/suggestions?province=Da%20Nang&dayId=301&mealSlot=lunch&q=bun" +
            "&tagIds=2&excludeTagIds=5&maxPrice=50000&lat=16.06&lng=108.22&radiusKm=10&limit=20");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("requestId").GetGuid().Should().Be(RoadmapApiFactory.SuggestionRequestId);
        data.GetProperty("items")[0].GetProperty("id").GetInt32().Should().Be(105);
        data.GetProperty("items")[0].GetProperty("rank").GetInt32().Should().Be(1);
        data.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        _factory.GetSuggestions.LastCall.Should().BeEquivalentTo(new
        {
            RoadmapId = 88L,
            Province = "Da Nang",
            DayId = (long?)301,
            MealSlot = "lunch",
            Limit = (int?)20
        });
    }

    // TEST-13: Bind RM-07 route and body values and serialize RoadmapItem with HTTP 201.
    /// <summary>Verifies RM-07 request binding, delegation, status, and complete success JSON shape.</summary>
    [Fact]
    public async Task AddItem_WithValidBody_ShouldSerializeCreatedRoadmapItem()
    {
        using var request = JsonRequest("POST", "/api/v1/roadmaps/88/days/301/items", $$"""
            {"dishId":105,"mealSlot":"lunch","sortOrder":2,"recoRequestId":"{{RoadmapApiFactory.SuggestionRequestId}}"}
            """);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("id").GetInt64().Should().Be(1502);
        data.GetProperty("dayId").GetInt64().Should().Be(301);
        data.GetProperty("source").GetString().Should().Be("manual");
        data.GetProperty("dish").GetProperty("status").GetString().Should().Be("active");
        _factory.AddItem.LastCall.Should().BeEquivalentTo(new
        {
            RoadmapId = 88L,
            DayId = 301L,
            DishId = 105,
            MealSlot = "lunch",
            SortOrder = (short?)2,
            RecoRequestId = (Guid?)RoadmapApiFactory.SuggestionRequestId
        });
    }

    // TEST-14: Preserve ROADMAP_NOT_FOUND through the RM-03 exception middleware boundary.
    /// <summary>Verifies owned-resource absence uses the documented 404 envelope.</summary>
    [Fact]
    public async Task GetDetail_WithMissingRoadmap_ShouldReturnNotFoundEnvelope()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/roadmaps/404");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertErrorCode(response, "ROADMAP_NOT_FOUND");
    }

    // TEST-15: Resolve every implemented roadmap use case, validator, repository, and controller dependency.
    /// <summary>Verifies all module registrations are connected in the production dependency graph.</summary>
    [Fact]
    public void Services_ShouldResolveRoadmapDependencies()
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        provider.GetRequiredService<IValidator<CreateRoadmapRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<GetMyRoadmapsRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<GetRoadmapDayRouteRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<GetRoadmapSuggestionsRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<AddRoadmapItemRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<UpdateRoadmapItemRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<UpdateRoadmapRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IDeleteRoadmapUseCase>().Should().NotBeNull();
        provider.GetRequiredService<IGetRoadmapItemDetailUseCase>().Should().NotBeNull();
        provider.GetRequiredService<IUpdateRoadmapItemUseCase>().Should().NotBeNull();
        provider.GetRequiredService<IDeleteRoadmapItemUseCase>().Should().NotBeNull();
        provider.GetRequiredService<IRoadmapRepository>().Should().NotBeNull();
        typeof(RoadmapsController).GetConstructors().Single().GetParameters()
            .Select(parameter => provider.GetService(parameter.ParameterType))
            .Should().NotContainNulls();
    }

    // TEST-16: Publish all implemented roadmap operations through runtime routing and OpenAPI.
    /// <summary>Verifies the endpoints consumed by Scalar are discoverable with their documented verbs.</summary>
    [Fact]
    public async Task RuntimeAndOpenApi_ShouldPublishAllRoadmapOperations()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?
                .ControllerTypeInfo.AsType() == typeof(RoadmapsController))
            .ToArray();
        endpoints.Should().HaveCount(11);

        using var response = await _factory.CreateClient().GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        paths.GetProperty("/api/v1/roadmaps").TryGetProperty("post", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps").TryGetProperty("get", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps/{roadmapId}").TryGetProperty("patch", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps/{roadmapId}").TryGetProperty("get", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps/{roadmapId}").TryGetProperty("delete", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps/{roadmapId}/days/{dayId}/route")
            .TryGetProperty("get", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps/{roadmapId}/suggestions")
            .TryGetProperty("get", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps/{roadmapId}/days/{dayId}/items")
            .TryGetProperty("post", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps/{roadmapId}/items/{itemId}")
            .TryGetProperty("get", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps/{roadmapId}/items/{itemId}")
            .TryGetProperty("patch", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/roadmaps/{roadmapId}/items/{itemId}")
            .TryGetProperty("delete", out _).Should().BeTrue();
    }

    // TEST-17: Serialize every RM-12 section and bind both route values.
    /// <summary>Verifies RM-12 routing, delegation, nullable values, and complete nested response JSON.</summary>
    [Fact]
    public async Task GetItemDetail_WithOwnedItem_ShouldSerializeCompleteDetail()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/roadmaps/88/items/1502");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("id").GetInt64().Should().Be(1502);
        data.GetProperty("myFeedback").GetString().Should().Be("like");
        data.GetProperty("dish").GetProperty("status").GetString().Should().Be("hidden");
        data.GetProperty("dish").GetProperty("rank").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("dish").GetProperty("restaurant").GetProperty("distanceKm")
            .ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("restaurant").GetProperty("businessHours")[0]
            .GetProperty("openTime").GetString().Should().Be("06:00");
        data.GetProperty("canRate").GetBoolean().Should().BeFalse();
        data.GetProperty("myReview").GetProperty("status").GetString().Should().Be("visible");
        _factory.GetItemDetail.LastCall.Should().Be(new RoadmapApiFactory.ItemDetailCall(88, 1502));
    }

    // TEST-18: Preserve ROADMAP_ITEM_NOT_FOUND through the RM-12 HTTP boundary.
    /// <summary>Verifies missing, mismatched, and foreign items use the sole documented 404 envelope.</summary>
    [Fact]
    public async Task GetItemDetail_WithoutOwnedMatchingItem_ShouldReturnNotFoundEnvelope()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/roadmaps/88/items/404");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertErrorCode(response, "ROADMAP_ITEM_NOT_FOUND");
    }

    // TEST-19: Bind every RM-08 route and body value and serialize the replacement RoadmapItem.
    /// <summary>Verifies RM-08 request binding, delegation, status, and the new item identity in JSON.</summary>
    [Fact]
    public async Task UpdateItem_WithReplacementBody_ShouldSerializeUpdatedRoadmapItem()
    {
        using var request = JsonRequest("PATCH", "/api/v1/roadmaps/88/items/1502", $$"""
            {"dishId":120,"recoRequestId":"{{RoadmapApiFactory.SuggestionRequestId}}","dayId":302,"mealSlot":"lunch","sortOrder":1}
            """);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("id").GetInt64().Should().Be(1600);
        data.GetProperty("dayId").GetInt64().Should().Be(302);
        data.GetProperty("mealSlot").GetString().Should().Be("lunch");
        data.GetProperty("dish").GetProperty("id").GetInt32().Should().Be(120);
        _factory.UpdateItem.LastCall.Should().BeEquivalentTo(new
        {
            RoadmapId = 88L,
            ItemId = 1502L,
            DishId = (int?)120,
            RecoRequestId = (Guid?)RoadmapApiFactory.SuggestionRequestId,
            DayId = (long?)302,
            MealSlot = "lunch",
            SortOrder = (short?)1
        });
    }

    // TEST-20: Serialize the RM-09 success envelope with explicit null data.
    /// <summary>Verifies DELETE routing, both route values, delegation, and the documented response JSON shape.</summary>
    [Fact]
    public async Task DeleteItem_WithOwnedMatchingItem_ShouldSerializeNullDataSuccessEnvelope()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/api/v1/roadmaps/88/items/1502");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("message").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("errorCode").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        _factory.DeleteItem.LastCall.Should().Be(new RoadmapApiFactory.ItemDetailCall(88, 1502));
    }

    // TEST-21: Preserve ROADMAP_ITEM_NOT_FOUND through the RM-09 HTTP boundary.
    /// <summary>Verifies missing, mismatched, foreign, and deleted-parent items use the sole documented 404 envelope.</summary>
    [Fact]
    public async Task DeleteItem_WithoutOwnedMatchingItem_ShouldReturnNotFoundEnvelope()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/roadmaps/88/items/404");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("TRAVELER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertErrorCode(response, "ROADMAP_ITEM_NOT_FOUND");
    }

    /// <summary>Builds an HTTP request with an application/json body.</summary>
    private static HttpRequestMessage JsonRequest(string method, string path, string json) => new(new HttpMethod(method), path)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>Asserts the standard API error envelope and requested error code.</summary>
    private static async Task AssertErrorCode(HttpResponseMessage response, string expected)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("errorCode").GetString().Should().Be(expected);
    }

    /// <summary>Creates a signed access token with the role used by the HTTP scenario.</summary>
    private static string Token(string role)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(RoadmapApiFactory.Secret)),
            SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            RoadmapApiFactory.Issuer,
            RoadmapApiFactory.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "12"), new Claim("role", role)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials));
    }
}

public sealed class RoadmapApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "ANGI.RoadmapHttpTests";
    public const string Audience = "ANGI.RoadmapHttpTests.Client";
    public const string Secret = "ANGI-104-http-contract-test-secret-key-123456789";
    public static readonly Guid SuggestionRequestId = Guid.Parse("6f0c2b1e-8d7a-4c1e-9b3f-2a5d7e9c1f00");

    /// <summary>Gets the stateful RM-04 stub used to inspect bound PATCH requests.</summary>
    public StubUpdateRoadmapUseCase Update { get; } = new();

    /// <summary>Gets the stateful RM-02 stub used to inspect bound query parameters.</summary>
    public StubGetMyRoadmapsUseCase GetMyRoadmaps { get; } = new();

    /// <summary>Gets the stateful RM-03 stub used to inspect route-value binding.</summary>
    public StubGetRoadmapDetailUseCase GetDetail { get; } = new();

    /// <summary>Gets the stateful RM-12 stub used to inspect both item-detail route values.</summary>
    public StubGetRoadmapItemDetailUseCase GetItemDetail { get; } = new();

    /// <summary>Gets the stateful RM-14 stub used to inspect route and query binding.</summary>
    public StubGetRoadmapDayRouteUseCase GetDayRoute { get; } = new();

    /// <summary>Gets the stateful RM-06 stub used to inspect route and query binding.</summary>
    public StubGetRoadmapSuggestionsUseCase GetSuggestions { get; } = new();

    /// <summary>Gets the stateful RM-07 stub used to inspect route and body binding.</summary>
    public StubAddRoadmapItemUseCase AddItem { get; } = new();

    /// <summary>Gets the stateful RM-08 stub used to inspect route and body binding.</summary>
    public StubUpdateRoadmapItemUseCase UpdateItem { get; } = new();

    /// <summary>Gets the stateful RM-05 stub used to inspect route-value binding.</summary>
    public StubDeleteRoadmapUseCase Delete { get; } = new();

    /// <summary>Gets the stateful RM-09 stub used to inspect both delete-item route values.</summary>
    public StubDeleteRoadmapItemUseCase DeleteItem { get; } = new();

    /// <summary>Provides JWT values before Program performs its immediate authentication configuration.</summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var previousIssuer = Environment.GetEnvironmentVariable("Jwt__Issuer");
        var previousAudience = Environment.GetEnvironmentVariable("Jwt__Audience");
        var previousSecret = Environment.GetEnvironmentVariable("Jwt__SecretKey");
        Environment.SetEnvironmentVariable("Jwt__Issuer", Issuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", Audience);
        Environment.SetEnvironmentVariable("Jwt__SecretKey", Secret);
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Jwt__Issuer", previousIssuer);
            Environment.SetEnvironmentVariable("Jwt__Audience", previousAudience);
            Environment.SetEnvironmentVariable("Jwt__SecretKey", previousSecret);
        }
    }

    /// <summary>Configures deterministic authentication and replaces database-dependent use cases.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Error);
        });
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:SecretKey"] = Secret,
                ["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEnsureActiveAccountUseCase>();
            services.RemoveAll<ICreateRoadmapUseCase>();
            services.RemoveAll<IGetMyRoadmapsUseCase>();
            services.RemoveAll<IGetRoadmapDetailUseCase>();
            services.RemoveAll<IGetRoadmapItemDetailUseCase>();
            services.RemoveAll<IGetRoadmapDayRouteUseCase>();
            services.RemoveAll<IGetRoadmapSuggestionsUseCase>();
            services.RemoveAll<IAddRoadmapItemUseCase>();
            services.RemoveAll<IUpdateRoadmapItemUseCase>();
            services.RemoveAll<IUpdateRoadmapUseCase>();
            services.RemoveAll<IDeleteRoadmapUseCase>();
            services.RemoveAll<IDeleteRoadmapItemUseCase>();
            services.AddScoped<IEnsureActiveAccountUseCase, NoOpAccountCheck>();
            services.AddScoped<ICreateRoadmapUseCase, StubCreateRoadmapUseCase>();
            services.AddSingleton(GetMyRoadmaps);
            services.AddSingleton<IGetMyRoadmapsUseCase>(GetMyRoadmaps);
            services.AddSingleton(GetDetail);
            services.AddSingleton<IGetRoadmapDetailUseCase>(GetDetail);
            services.AddSingleton(GetItemDetail);
            services.AddSingleton<IGetRoadmapItemDetailUseCase>(GetItemDetail);
            services.AddSingleton(GetDayRoute);
            services.AddSingleton<IGetRoadmapDayRouteUseCase>(GetDayRoute);
            services.AddSingleton(GetSuggestions);
            services.AddSingleton<IGetRoadmapSuggestionsUseCase>(GetSuggestions);
            services.AddSingleton(AddItem);
            services.AddSingleton<IAddRoadmapItemUseCase>(AddItem);
            services.AddSingleton(UpdateItem);
            services.AddSingleton<IUpdateRoadmapItemUseCase>(UpdateItem);
            services.AddSingleton(Update);
            services.AddSingleton<IUpdateRoadmapUseCase>(Update);
            services.AddSingleton(Delete);
            services.AddSingleton<IDeleteRoadmapUseCase>(Delete);
            services.AddSingleton(DeleteItem);
            services.AddSingleton<IDeleteRoadmapItemUseCase>(DeleteItem);
        });
    }

    private sealed class NoOpAccountCheck : IEnsureActiveAccountUseCase
    {
        /// <summary>Allows contract tests to bypass database-backed account checks.</summary>
        public Task ExecuteAsync(int userId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubCreateRoadmapUseCase : ICreateRoadmapUseCase
    {
        /// <summary>Returns deterministic empty days for RM-01 serialization tests.</summary>
        public Task<RoadmapDetailDto> ExecuteAsync(CreateRoadmapRequestDto request, CancellationToken ct) =>
            Task.FromResult(Detail(request.NumDays));
    }

    public sealed class StubGetMyRoadmapsUseCase : IGetMyRoadmapsUseCase
    {
        public GetMyRoadmapsRequestDto? LastRequest { get; private set; }

        /// <summary>Captures the bound query and returns a deterministic RM-02 page.</summary>
        public Task<Paged<RoadmapSummaryDto>> ExecuteAsync(
            GetMyRoadmapsRequestDto request,
            CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new Paged<RoadmapSummaryDto>
            {
                Items =
                [
                    new RoadmapSummaryDto
                    {
                        Id = 88,
                        Title = "Trip",
                        NumDays = 3,
                        OriginMode = "pinned",
                        OriginLatitude = 16.061204m,
                        OriginLongitude = 108.227531m
                    }
                ],
                Page = request.Page,
                PageSize = request.PageSize,
                TotalItems = 11,
                TotalPages = 2
            });
        }
    }

    public sealed class StubUpdateRoadmapUseCase : IUpdateRoadmapUseCase
    {
        public UpdateRoadmapRequestDto? LastRequest { get; private set; }

        /// <summary>Captures the bound PATCH request and returns a deterministic detail.</summary>
        public Task<RoadmapDetailDto> ExecuteAsync(long roadmapId, UpdateRoadmapRequestDto request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Detail(1));
        }
    }

    public sealed class StubGetRoadmapDetailUseCase : IGetRoadmapDetailUseCase
    {
        public long? LastRoadmapId { get; private set; }

        /// <summary>Captures the route value, returns deterministic detail, or emits the RM-03 missing error.</summary>
        public Task<RoadmapDetailDto> ExecuteAsync(long roadmapId, CancellationToken ct)
        {
            LastRoadmapId = roadmapId;
            if (roadmapId == 404)
            {
                throw new ANGI.Application.Common.Exceptions.NotFoundException(
                    "ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
            }

            return Task.FromResult(Detail(1));
        }
    }

    public sealed class StubGetRoadmapDayRouteUseCase : IGetRoadmapDayRouteUseCase
    {
        public RouteCall? LastCall { get; private set; }

        /// <summary>Validates and captures RM-14 input before returning deterministic route metrics.</summary>
        public async Task<GetRoadmapDayRouteResponseDto> ExecuteAsync(
            long roadmapId,
            long dayId,
            GetRoadmapDayRouteRequestDto request,
            CancellationToken ct)
        {
            await new ANGI.Application.UseCases.Validators.Roadmap.GetRoadmapDayRouteRequestDtoValidator()
                .ValidateAndThrowAsync(request, ct);
            LastCall = new RouteCall(roadmapId, dayId, request.OriginLat, request.OriginLng, request.Profile);
            return new GetRoadmapDayRouteResponseDto
            {
                DayId = dayId,
                Profile = request.Profile!.ToLowerInvariant(),
                Origin = request.OriginLat.HasValue
                    ? new RoadmapRouteOriginDto
                    {
                        Latitude = request.OriginLat.Value,
                        Longitude = request.OriginLng!.Value,
                        Source = "gps"
                    }
                    : null,
                Legs =
                [
                    new RoadmapRouteLegDto
                    {
                        FromItemId = null,
                        ToItemId = 1502,
                        DistanceMeters = 2350,
                        DurationSeconds = 540
                    }
                ],
                TotalDistanceMeters = 2350,
                TotalDurationSeconds = 540
            };
        }
    }

    public sealed class StubGetRoadmapItemDetailUseCase : IGetRoadmapItemDetailUseCase
    {
        public ItemDetailCall? LastCall { get; private set; }

        /// <summary>Captures both RM-12 route values, returns complete detail, or emits the documented missing error.</summary>
        public Task<RoadmapItemDetailDto> ExecuteAsync(long roadmapId, long itemId, CancellationToken ct)
        {
            LastCall = new ItemDetailCall(roadmapId, itemId);
            if (itemId == 404)
            {
                throw new ANGI.Application.Common.Exceptions.NotFoundException(
                    "ROADMAP_ITEM_NOT_FOUND", "Không tìm thấy món trong roadmap.");
            }

            var review = new ANGI.Application.DTOs.Restaurant.ReviewDto
            {
                Id = 640,
                RestaurantId = 7,
                User = new ANGI.Application.DTOs.Account.UserSummaryDto { Id = 12, DisplayName = "Traveler" },
                Rating = 5,
                Status = "visible",
                CreatedAt = new DateTime(2026, 10, 3, 2, 0, 0, DateTimeKind.Utc)
            };
            return Task.FromResult(new RoadmapItemDetailDto
            {
                Id = itemId,
                DayId = 301,
                DayNumber = 1,
                MealSlot = "breakfast",
                Source = "manual",
                MyFeedback = "like",
                CreatedAt = new DateTime(2026, 10, 2, 2, 10, 0, DateTimeKind.Utc),
                Dish = new ANGI.Application.DTOs.Discovery.DishDetailDto
                {
                    Id = 105,
                    Name = "Dish",
                    DishForm = "soup",
                    Status = "hidden",
                    Restaurant = new ANGI.Application.DTOs.Restaurant.RestaurantCardDto
                    {
                        Id = 7,
                        Name = "Restaurant",
                        Slug = "restaurant",
                        AddressLine = "12 Street",
                        OperatingStatus = "open",
                        DistanceKm = null
                    }
                },
                Restaurant = new ANGI.Application.DTOs.Restaurant.RestaurantDetailDto
                {
                    Id = 7,
                    Name = "Restaurant",
                    Slug = "restaurant",
                    AddressLine = "12 Street",
                    OperatingStatus = "open",
                    BusinessHours =
                    [
                        new ANGI.Application.DTOs.Restaurant.BusinessHourDto
                        {
                            DayOfWeek = 1,
                            OpenTime = "06:00",
                            CloseTime = "13:30"
                        }
                    ],
                    MyReview = review
                },
                CanRate = false,
                MyReview = review
            });
        }
    }

    public sealed class StubDeleteRoadmapUseCase : IDeleteRoadmapUseCase
    {
        public long? LastRoadmapId { get; private set; }

        /// <summary>Captures the route value or emits the RM-05 owned-resource error.</summary>
        public Task ExecuteAsync(long roadmapId, CancellationToken ct)
        {
            LastRoadmapId = roadmapId;
            if (roadmapId == 404)
            {
                throw new ANGI.Application.Common.Exceptions.NotFoundException(
                    "ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
            }

            return Task.CompletedTask;
        }
    }

    public sealed class StubDeleteRoadmapItemUseCase : IDeleteRoadmapItemUseCase
    {
        public ItemDetailCall? LastCall { get; private set; }

        /// <summary>Captures both route values or emits the RM-09 owned-resource error.</summary>
        public Task ExecuteAsync(long roadmapId, long itemId, CancellationToken ct)
        {
            LastCall = new ItemDetailCall(roadmapId, itemId);
            if (itemId == 404)
            {
                throw new ANGI.Application.Common.Exceptions.NotFoundException(
                    "ROADMAP_ITEM_NOT_FOUND", "Không tìm thấy món trong roadmap.");
            }

            return Task.CompletedTask;
        }
    }

    public sealed class StubGetRoadmapSuggestionsUseCase : IGetRoadmapSuggestionsUseCase
    {
        public SuggestionCall? LastCall { get; private set; }

        /// <summary>Captures every RM-06 query value and returns a representative ranked DishCard.</summary>
        public Task<GetRoadmapSuggestionsResponseDto> ExecuteAsync(
            long roadmapId,
            GetRoadmapSuggestionsRequestDto request,
            CancellationToken ct)
        {
            LastCall = new SuggestionCall(roadmapId, request.Province, request.DayId, request.MealSlot, request.Limit);
            return Task.FromResult(new GetRoadmapSuggestionsResponseDto
            {
                RequestId = SuggestionRequestId,
                Items =
                [
                    new ANGI.Application.DTOs.Discovery.DishCardDto
                    {
                        Id = 105,
                        Name = "Bún bò Huế",
                        Price = 45_000,
                        DishForm = "soup",
                        IsAvailable = true,
                        Restaurant = new ANGI.Application.DTOs.Discovery.RestaurantMiniDto
                        {
                            Id = 7,
                            Name = "Bún Bò Bà Diệu",
                            RatingAvg = 4.6m
                        },
                        Rank = 1
                    }
                ]
            });
        }
    }

    public sealed class StubAddRoadmapItemUseCase : IAddRoadmapItemUseCase
    {
        public AddItemCall? LastCall { get; private set; }

        /// <summary>Captures every RM-07 route/body value and returns a representative RoadmapItem.</summary>
        public Task<RoadmapItemDto> ExecuteAsync(
            long roadmapId,
            long dayId,
            AddRoadmapItemRequestDto request,
            CancellationToken ct)
        {
            LastCall = new AddItemCall(
                roadmapId, dayId, request.DishId, request.MealSlot, request.SortOrder, request.RecoRequestId);
            return Task.FromResult(new RoadmapItemDto
            {
                Id = 1502,
                DayId = dayId,
                DayNumber = 1,
                MealSlot = request.MealSlot,
                SortOrder = request.SortOrder ?? 0,
                Source = "manual",
                RecoRequestId = request.RecoRequestId,
                Dish = new RoadmapDishDto
                {
                    Id = request.DishId,
                    Name = "Bún bò Huế",
                    DishForm = "soup",
                    Status = "active"
                }
            });
        }
    }

    public sealed class StubUpdateRoadmapItemUseCase : IUpdateRoadmapItemUseCase
    {
        public UpdateItemCall? LastCall { get; private set; }

        /// <summary>Captures every RM-08 route/body value and returns a representative replacement item.</summary>
        public Task<RoadmapItemDto> ExecuteAsync(
            long roadmapId,
            long itemId,
            UpdateRoadmapItemRequestDto request,
            CancellationToken ct)
        {
            LastCall = new UpdateItemCall(
                roadmapId,
                itemId,
                request.DishId,
                request.RecoRequestId,
                request.DayId,
                request.MealSlot,
                request.SortOrder);
            return Task.FromResult(new RoadmapItemDto
            {
                Id = request.DishId.HasValue ? 1600 : itemId,
                DayId = request.DayId ?? 301,
                DayNumber = request.DayId == 302 ? (short)2 : (short)1,
                MealSlot = request.MealSlot ?? "breakfast",
                SortOrder = request.SortOrder ?? 0,
                Source = "manual",
                RecoRequestId = request.RecoRequestId,
                Dish = new RoadmapDishDto
                {
                    Id = request.DishId ?? 105,
                    Name = "Replacement dish",
                    DishForm = "soup",
                    Status = "active"
                }
            });
        }
    }

    public sealed record RouteCall(
        long RoadmapId,
        long DayId,
        decimal? OriginLat,
        decimal? OriginLng,
        string? Profile);

    public sealed record SuggestionCall(
        long RoadmapId,
        string Province,
        long? DayId,
        string? MealSlot,
        int? Limit);

    public sealed record AddItemCall(
        long RoadmapId,
        long DayId,
        int DishId,
        string MealSlot,
        short? SortOrder,
        Guid? RecoRequestId);

    public sealed record UpdateItemCall(
        long RoadmapId,
        long ItemId,
        int? DishId,
        Guid? RecoRequestId,
        long? DayId,
        string? MealSlot,
        short? SortOrder);

    public sealed record ItemDetailCall(long RoadmapId, long ItemId);

    /// <summary>Builds a deterministic empty-roadmap response for HTTP serialization.</summary>
    private static RoadmapDetailDto Detail(short numDays) => new()
    {
        Overview = new RoadmapSummaryDto { Id = 88, Title = "Trip", NumDays = numDays, OriginMode = "none" },
        Itinerary = Enumerable.Range(1, numDays).Select(day => new RoadmapDayDto { DayNumber = (short)day }).ToArray(),
        Budget = new RoadmapBudgetDto()
    };
}
