using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>
/// DISH-03 through HTTP and PostgreSQL. The test host's reco keeps the candidate order and returns no request id or
/// score, so the roll draws evenly among the filtered candidates; filters narrowed to one dish make the result exact.
/// </summary>
public sealed class RollDishEndToEndTests : IClassFixture<PostgresApiFactory>
{
    // Đà Nẵng centre, and a point about 10 km north of it.
    private const decimal NearLat = 16.0678m, NearLng = 108.2208m, FarLat = 16.1578m;

    private readonly PostgresApiFactory _factory;

    public RollDishEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record World(Dictionary<string, int> Dish, Dictionary<string, short> Tag);

    // TEST-01: mealTime, tagIds (any), excludeTagIds, maxPrice and excludeDishIds narrow the candidates; the roll is stored.
    [PostgresEndToEndFact]
    public async Task Roll_WithFilters_ShouldRollAMatchingDishAndStoreIt()
    {
        var world = await SeedAsync();
        using var client = await TravelerClientAsync();
        var (d, t) = (world.Dish, world.Tag);

        (await RollAsync(client, new { mealTime = "dinner" })).Dish.Id.Should().Be(d["comGa"]);
        (await RollAsync(client, new { tagIds = new[] { t["soup"] }, maxPrice = 35000 })).Dish.Id.Should().Be(d["bunRieu"]);
        (await RollAsync(client, new { mealTime = "lunch", excludeTagIds = new[] { t["soup"] } })).Dish.Id.Should().Be(d["bunCha"]);
        var roll = await RollAsync(client, new { mealTime = "lunch", excludeDishIds = new[] { d["bunBo"], d["bunCha"] } });
        roll.Dish.Id.Should().Be(d["bunRieu"]);
        roll.RequestId.Should().BeNull();
        roll.Dish.Rank.Should().Be(1);
        roll.Dish.Restaurant.DistanceKm.Should().BeNull();
        roll.Dish.Tags.Select(tag => tag.Code).Should().BeEquivalentTo("lunch", "soup");

        var anyLunch = await RollAsync(client, new { mealTime = "lunch" });
        new[] { d["bunBo"], d["bunCha"], d["bunRieu"] }.Should().Contain(anyLunch.Dish.Id);
        anyLunch.Dish.Rank.Should().BeInRange(1, 3);

        var stored = await _factory.ExecuteDbAsync(context =>
            context.DishRolls.AsNoTracking().SingleAsync(value => value.Id == roll.RollId));
        stored.DishId.Should().Be(d["bunRieu"]);
        stored.RecoRequestId.Should().BeNull();
        (await _factory.ExecuteDbAsync(context => context.DishRolls.CountAsync())).Should().Be(5);
    }

    // TEST-02: Coordinates keep restaurants within 3 km by default, or radiusKm; distanceKm is returned.
    [PostgresEndToEndFact]
    public async Task Roll_NearAPoint_ShouldUseTheRadius()
    {
        var world = await SeedAsync();
        using var client = await TravelerClientAsync();
        var d = world.Dish;

        var near = await RollAsync(client, new
        {
            latitude = FarLat, longitude = NearLng, mealTime = "lunch"
        });
        near.Dish.Id.Should().Be(d["bunRieu"]);
        near.Dish.Restaurant.DistanceKm.Should().Be(0m);

        using var none = await PostAsync(client, new
        {
            latitude = FarLat, longitude = NearLng, excludeDishIds = new[] { d["bunRieu"] }
        });
        none.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(none)).Should().Be("NO_CANDIDATE");

        var wide = await RollAsync(client, new
        {
            latitude = FarLat, longitude = NearLng, radiusKm = 15, mealTime = "dinner"
        });
        wide.Dish.Id.Should().Be(d["comGa"]);
        wide.Dish.Restaurant.DistanceKm.Should().Be(10.0m);
    }

    // TEST-03: Hidden, sold-out, closed and unverified dishes are never rolled; no match is 404 without a stored roll.
    [PostgresEndToEndFact]
    public async Task Roll_WithoutCandidate_ShouldReturnNoCandidate()
    {
        var world = await SeedAsync();
        using var client = await TravelerClientAsync();
        var d = world.Dish;

        using var response = await PostAsync(client, new
        {
            excludeDishIds = new[] { d["comGa"], d["bunBo"], d["bunCha"], d["bunRieu"] }
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be("NO_CANDIDATE");
        (await _factory.ExecuteDbAsync(context => context.DishRolls.CountAsync())).Should().Be(0);
    }

    // TEST-04: Guests get 401, other roles 403, and an out-of-contract body 400.
    [PostgresEndToEndFact]
    public async Task Roll_ShouldFollowTheAccessAndValidationContract()
    {
        await SeedAsync();
        using var client = _factory.CreateClient();

        using var anonymous = await PostAsync(client, new { });
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var owner = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "RESTAURANT_OWNER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        using var asOwner = await PostAsync(client, new { });
        asOwner.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var traveler = await TravelerClientAsync();
        using var badMeal = await PostAsync(traveler, new { mealTime = "brunch" });
        badMeal.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var radiusAlone = await PostAsync(traveler, new { radiusKm = 3 });
        radiusAlone.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var noBody = await traveler.PostAsync("/api/v1/dishes/roll", null);
        noBody.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Restaurant A (centre): comGa (35k, dinner), bunBo (45k, lunch + soup), bunCha (40k, lunch + pork), plus a
    /// sold-out and a hidden dish. Restaurant B (10 km north): bunRieu (30k, lunch + soup). A closed and an unverified
    /// restaurant each have one lunch dish. Only the four named dishes are candidates.
    /// </summary>
    private async Task<World> SeedAsync()
    {
        await _factory.ResetDatabaseAsync();
        var a = await RestaurantAsync("Bún Bò Bà Diệu", NearLat, NearLng);
        var b = await RestaurantAsync("Quán Riêu Ngon", FarLat, NearLng);
        var closed = await RestaurantAsync("Quán Đóng Cửa", NearLat, NearLng);
        var unverified = await RestaurantAsync("Quán Chưa Duyệt", NearLat, NearLng);

        return await _factory.ExecuteDbAsync(async context =>
        {
            var restaurants = await context.Restaurants.ToDictionaryAsync(r => r.Id);
            foreach (var id in new[] { a, b, closed })
            {
                restaurants[id].VerificationStatus = RestaurantVerificationStatus.Verified;
            }

            restaurants[closed].OperatingStatus = RestaurantOperatingStatus.TemporarilyClosed;
            restaurants[unverified].VerificationStatus = RestaurantVerificationStatus.Unverified;

            var tags = new Dictionary<string, Tag>
            {
                ["lunch"] = new() { TagType = TagType.MealTime, Code = "lunch", Name = "Bữa trưa" },
                ["dinner"] = new() { TagType = TagType.MealTime, Code = "dinner", Name = "Bữa tối" },
                ["soup"] = new() { TagType = TagType.Category, Code = "soup", Name = "Món nước" },
                ["pork"] = new() { TagType = TagType.Dietary, Code = "pork", Name = "Có thịt heo" }
            };
            Dish New(long restaurantId, string name, decimal price, params string[] tagCodes)
            {
                var dish = new Dish { RestaurantId = restaurantId, Name = name, Price = price };
                foreach (var code in tagCodes)
                {
                    dish.DishTags.Add(new DishTag { Tag = tags[code] });
                }

                return dish;
            }

            var dishes = new Dictionary<string, Dish>
            {
                ["comGa"] = New(a, "Cơm gà", 35000, "dinner"),
                ["bunBo"] = New(a, "Bún bò Huế", 45000, "lunch", "soup"),
                ["bunCha"] = New(a, "Bún chả", 40000, "lunch", "pork"),
                ["bunRieu"] = New(b, "Bún riêu", 30000, "lunch", "soup"),
                ["soldOut"] = New(b, "Bún mắm", 30000, "lunch"),
                ["hidden"] = New(b, "Bún ẩn", 30000, "lunch"),
                ["closed"] = New(closed, "Bún quán đóng", 30000, "lunch"),
                ["unverified"] = New(unverified, "Bún chưa duyệt", 30000, "lunch")
            };
            dishes["soldOut"].IsAvailable = false;
            dishes["hidden"].Status = DishStatus.Hidden;
            context.Dishes.AddRange(dishes.Values);
            await context.SaveChangesAsync();
            return new World(
                dishes.ToDictionary(pair => pair.Key, pair => pair.Value.Id),
                tags.ToDictionary(pair => pair.Key, pair => pair.Value.Id));
        });
    }

    private async Task<long> RestaurantAsync(string name, decimal lat, decimal lng)
    {
        using var client = _factory.CreateClient();
        var owner = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "RESTAURANT_OWNER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        using var response = await client.PostAsJsonAsync("/api/v1/owner/restaurant", new RegisterRestaurantRequestDto
        {
            Name = name,
            Phone = "0900000000",
            AddressLine = "1 Integration Street",
            ProvinceName = "Đà Nẵng",
            Latitude = lat,
            Longitude = lng
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<OwnerRestaurantDto>>())!.Data!.Id;
    }

    private async Task<HttpClient> TravelerClientAsync()
    {
        var client = _factory.CreateClient();
        var traveler = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", traveler.AccessToken);
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, object body) =>
        client.PostAsJsonAsync("/api/v1/dishes/roll", body);

    private static async Task<RollDishResponseDto> RollAsync(HttpClient client, object body)
    {
        using var response = await PostAsync(client, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApiResponse<RollDishResponseDto>>())!.Data!;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("errorCode").GetString();
    }
}
