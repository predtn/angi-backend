using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>
/// UC View Corresponding Restaurant Detail (ANGI-64): from a dish (DISH-02) the app opens its restaurant (REST-02)
/// with DishDetail.restaurant.id. Both endpoints must agree on which restaurants are public.
/// </summary>
public sealed class CorrespondingRestaurantEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public CorrespondingRestaurantEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Open the restaurant of a dish with the id DISH-02 returns; REST-02 shows the same restaurant and its menu.
    /// <summary>Covers the guest flow dish → restaurant with include=menu.</summary>
    [PostgresEndToEndFact]
    public async Task DishThenRestaurant_ForGuest_ShouldOpenTheSameRestaurantWithTheDishInItsMenu()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();

        var dish = await DataAsync<DishDetailDto>(await client.GetAsync($"/api/v1/dishes/{seed.DishId}"));
        var restaurant = await DataAsync<RestaurantDetailDto>(
            await client.GetAsync($"/api/v1/restaurants/{dish.Restaurant.Id}?include=menu"));

        restaurant.Id.Should().Be(seed.RestaurantId);
        restaurant.Should().BeEquivalentTo(dish.Restaurant, options => options.ExcludingMissingMembers());
        restaurant.Menu.Should().NotBeNull();
        restaurant.Menu!.Select(item => item.Id).Should().Contain(seed.DishId).And.NotContain(seed.HiddenDishId);
        restaurant.MyReview.Should().BeNull();
    }

    // TEST-02: A Traveler token is optional on both endpoints and does not change the restaurant opened.
    /// <summary>Covers the traveler flow; without a review of their own myReview stays null.</summary>
    [PostgresEndToEndFact]
    public async Task DishThenRestaurant_ForTraveler_ShouldSucceedWithToken()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var traveler = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", traveler.AccessToken);

        var dish = await DataAsync<DishDetailDto>(await client.GetAsync($"/api/v1/dishes/{seed.DishId}"));
        var restaurant = await DataAsync<RestaurantDetailDto>(await client.GetAsync($"/api/v1/restaurants/{dish.Restaurant.Id}"));

        restaurant.Id.Should().Be(seed.RestaurantId);
        restaurant.Menu.Should().BeNull();
        restaurant.MyReview.Should().BeNull();
    }

    // TEST-03: When the restaurant stops being public, the dish and the restaurant disappear together.
    /// <summary>Hidden, unverified and deleted restaurants answer 404 on both DISH-02 and REST-02.</summary>
    [PostgresEndToEndFact]
    public async Task NonPublicRestaurant_ShouldBeNotFoundOnBothEndpoints()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var changes = new (Action<Domain.Entities.Restaurant> Break, Action<Domain.Entities.Restaurant> Restore)[]
        {
            (r => r.ModerationStatus = RestaurantModerationStatus.Hidden, r => r.ModerationStatus = RestaurantModerationStatus.Visible),
            (r => r.ModerationStatus = RestaurantModerationStatus.Suspended, r => r.ModerationStatus = RestaurantModerationStatus.Visible),
            (r => r.VerificationStatus = RestaurantVerificationStatus.Pending, r => r.VerificationStatus = RestaurantVerificationStatus.Verified),
            (r => r.DeletedAt = DateTime.UtcNow, r => r.DeletedAt = null)
        };

        foreach (var (breakIt, restore) in changes)
        {
            await DiscoverySeed.UpdateRestaurantAsync(_factory, seed.RestaurantId, breakIt);

            (await ErrorCodeAsync(client, $"/api/v1/dishes/{seed.DishId}")).Should().Be("DISH_NOT_FOUND");
            (await ErrorCodeAsync(client, $"/api/v1/restaurants/{seed.RestaurantId}")).Should().Be("RESTAURANT_NOT_FOUND");

            await DiscoverySeed.UpdateRestaurantAsync(_factory, seed.RestaurantId, restore);
            (await client.GetAsync($"/api/v1/dishes/{seed.DishId}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync($"/api/v1/restaurants/{seed.RestaurantId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    private static async Task<T> DataAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
            envelope!.Success.Should().BeTrue();
            return envelope.Data!;
        }
    }

    private static async Task<string?> ErrorCodeAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("errorCode").GetString();
    }
}
