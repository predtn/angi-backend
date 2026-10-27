using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Exercises DISH-02 through HTTP and PostgreSQL, above all which dishes are public.</summary>
public sealed class DishDetailEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public DishDetailEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Return a public dish to an anonymous caller with tags, ordered images and its restaurant card.
    /// <summary>Covers ANGI-60 with the real repository query, mapping and JSON contract.</summary>
    [PostgresEndToEndFact]
    public async Task GetDishDetail_ForPublicDish_ShouldReturnDishDetailAnonymously()
    {
        await _factory.ResetDatabaseAsync();
        var (restaurantId, dishId, _) = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/dishes/{dishId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dish = (await response.Content.ReadFromJsonAsync<ApiResponse<DishDetailDto>>())!.Data!;
        dish.Id.Should().Be(dishId);
        dish.Name.Should().Be("Bún bò Huế");
        dish.DishForm.Should().Be("soup");
        dish.Status.Should().Be("active");
        dish.Rank.Should().BeNull();
        dish.CoverUrl.Should().Be("https://cdn.test/dishes/cover.jpg");
        dish.Images.Should().Equal("https://cdn.test/dishes/first.jpg", "https://cdn.test/dishes/second.jpg");
        dish.Tags.Should().ContainSingle().Which.Should().BeEquivalentTo(new { TagType = "meal_time", Code = "lunch" });
        dish.Restaurant.Id.Should().Be(restaurantId);
        dish.Restaurant.Ward.Should().Be("Phường Bến Thành");
        dish.Restaurant.Slug.Should().NotBeNullOrWhiteSpace();
        dish.Restaurant.OperatingStatus.Should().Be("open");
        dish.Restaurant.DistanceKm.Should().BeNull();
    }

    // TEST-02: Answer 404 DISH_NOT_FOUND for a missing, hidden or removed dish and for a restaurant that is not public.
    /// <summary>Covers every non-public case the API Design lists for DISH-02.</summary>
    [PostgresEndToEndFact]
    public async Task GetDishDetail_ForNonPublicDish_ShouldReturnDishNotFound()
    {
        await _factory.ResetDatabaseAsync();
        var (restaurantId, dishId, hiddenDishId) = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();

        (await ErrorCodeAsync(client, dishId + 1000)).Should().Be("DISH_NOT_FOUND");
        (await ErrorCodeAsync(client, hiddenDishId)).Should().Be("DISH_NOT_FOUND");

        await DiscoverySeed.UpdateRestaurantAsync(_factory, restaurantId, r => r.ModerationStatus = RestaurantModerationStatus.Hidden);
        (await ErrorCodeAsync(client, dishId)).Should().Be("DISH_NOT_FOUND");
        await DiscoverySeed.UpdateRestaurantAsync(_factory, restaurantId, r => r.ModerationStatus = RestaurantModerationStatus.Visible);

        await DiscoverySeed.UpdateRestaurantAsync(_factory, restaurantId, r => r.VerificationStatus = RestaurantVerificationStatus.Unverified);
        (await ErrorCodeAsync(client, dishId)).Should().Be("DISH_NOT_FOUND");
        await DiscoverySeed.UpdateRestaurantAsync(_factory, restaurantId, r => r.VerificationStatus = RestaurantVerificationStatus.Verified);

        await DiscoverySeed.UpdateRestaurantAsync(_factory, restaurantId, r => r.DeletedAt = DateTime.UtcNow);
        (await ErrorCodeAsync(client, dishId)).Should().Be("DISH_NOT_FOUND");
    }

    // TEST-03: Restaurant cards (REST-01) and the restaurant inside dish cards (DISH-01) carry the ward.
    /// <summary>Addresses are province + ward since the 2025 merger, so lists show the ward under the name.</summary>
    [PostgresEndToEndFact]
    public async Task BrowseLists_ShouldIncludeTheRestaurantWard()
    {
        await _factory.ResetDatabaseAsync();
        var (restaurantId, dishId, _) = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();

        using var restaurants = await client.GetAsync("/api/v1/restaurants");
        restaurants.StatusCode.Should().Be(HttpStatusCode.OK);
        var cards = (await restaurants.Content.ReadFromJsonAsync<ApiResponse<Paged<RestaurantCardDto>>>())!.Data!.Items;
        cards.Should().ContainSingle(card => card.Id == restaurantId).Which.Ward.Should().Be("Phường Bến Thành");

        using var dishes = await client.GetAsync("/api/v1/dishes");
        dishes.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await dishes.Content.ReadFromJsonAsync<ApiResponse<BrowseDishesResponseDto>>())!.Data!.Items;
        items.Should().ContainSingle(item => item.Id == dishId).Which.Restaurant.Ward.Should().Be("Phường Bến Thành");
    }

    private static async Task<string?> ErrorCodeAsync(HttpClient client, int dishId)
    {
        using var response = await client.GetAsync($"/api/v1/dishes/{dishId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("errorCode").GetString();
    }
}
