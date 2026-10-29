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
/// DISH-01, CAT-02 and IMP-01 through HTTP and PostgreSQL. The test host's reco returns the candidates in the order
/// sent, so the responses show the candidate filter and its rating / like_count order directly.
/// </summary>
public sealed class BrowseDishesEndToEndTests : IClassFixture<PostgresApiFactory>
{
    // Đà Nẵng centre, and a point about 10 km north of it.
    private const decimal NearLat = 16.0678m, NearLng = 108.2208m, FarLat = 16.1578m;

    private readonly PostgresApiFactory _factory;

    public BrowseDishesEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record World(
        Dictionary<string, int> Dish,
        Dictionary<string, short> Tag);

    // TEST-01: Only public candidates, ordered by restaurant rating then like_count; q matches dish or restaurant names.
    [PostgresEndToEndFact]
    public async Task Browse_ShouldReturnOnlyCandidatesInRatingThenLikeOrder()
    {
        var world = await SeedAsync();
        using var client = _factory.CreateClient();

        (await IdsAsync(client, "")).Should().Equal(world.Dish["comGa"], world.Dish["bunBo"], world.Dish["bunCha"], world.Dish["bunRieu"]);
        // Case-insensitive on accented letters; "Bún" also matches restaurant A, so its dry "Cơm gà" comes too.
        (await IdsAsync(client, "?q=BÚN")).Should().HaveCount(4);
        (await IdsAsync(client, "?q=chả")).Should().Equal(world.Dish["bunCha"]);
        (await IdsAsync(client, "?q=bà diệu")).Should().Equal(world.Dish["comGa"], world.Dish["bunBo"], world.Dish["bunCha"]);
        (await IdsAsync(client, "?q=riêu ngon")).Should().Equal(world.Dish["bunRieu"]);
    }

    // TEST-02: Tags are OR within a type and AND across types; excluded tags and price, form, restaurant filters apply.
    [PostgresEndToEndFact]
    public async Task Browse_WithFilters_ShouldApplyTheDocumentedSemantics()
    {
        var world = await SeedAsync();
        using var client = _factory.CreateClient();
        var t = world.Tag;
        var d = world.Dish;

        (await IdsAsync(client, $"?tagIds={t["lunch"]}&tagIds={t["dinner"]}"))
            .Should().Equal(d["comGa"], d["bunBo"], d["bunCha"], d["bunRieu"]);
        (await IdsAsync(client, $"?tagIds={t["lunch"]}&tagIds={t["soup"]}")).Should().Equal(d["bunBo"], d["bunRieu"]);
        (await IdsAsync(client, $"?tagIds={t["lunch"]}&tagIds={t["dinner"]}&tagIds={t["soup"]}")).Should().Equal(d["bunBo"], d["bunRieu"]);
        (await IdsAsync(client, "?tagIds=32000")).Should().BeEmpty();
        (await IdsAsync(client, $"?excludeTagIds={t["pork"]}")).Should().NotContain(d["bunCha"]).And.HaveCount(3);
        (await IdsAsync(client, "?dishForm=soup")).Should().Equal(d["bunBo"], d["bunRieu"]);
        (await IdsAsync(client, "?minPrice=35000&maxPrice=45000")).Should().Equal(d["comGa"], d["bunBo"], d["bunCha"]);
    }

    // TEST-03: lat/lng keep restaurants within 5 km by default, or within radiusKm.
    [PostgresEndToEndFact]
    public async Task Browse_NearAPoint_ShouldUseTheRadius()
    {
        var world = await SeedAsync();
        using var client = _factory.CreateClient();

        (await IdsAsync(client, $"?lat={NearLat}&lng={NearLng}"))
            .Should().Equal(world.Dish["comGa"], world.Dish["bunBo"], world.Dish["bunCha"]);
        (await IdsAsync(client, $"?lat={NearLat}&lng={NearLng}&radiusKm=15")).Should().HaveCount(4);
    }

    // TEST-04: Page with the cursor until nextCursor is null; a bad cursor is 400 INVALID_CURSOR.
    [PostgresEndToEndFact]
    public async Task Browse_WithCursor_ShouldWalkEveryPageOnce()
    {
        var world = await SeedAsync();
        using var client = _factory.CreateClient();

        var first = await PageAsync(client, "?limit=3");
        var second = await PageAsync(client, $"?limit=3&cursor={first.NextCursor}");

        first.RequestId.Should().BeNull();
        first.Items.Select(item => (item.Id, item.Rank)).Should().Equal(
            (world.Dish["comGa"], 1), (world.Dish["bunBo"], 2), (world.Dish["bunCha"], 3));
        second.Items.Select(item => (item.Id, item.Rank)).Should().Equal((world.Dish["bunRieu"], 4));
        second.NextCursor.Should().BeNull();

        using var bad = await client.GetAsync("/api/v1/dishes?cursor=bogus");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(bad)).Should().Be("INVALID_CURSOR");
    }

    // TEST-05: CAT-02 lists tags by type then name; IMP-01 accepts a Traveler and refuses guests and owners.
    [PostgresEndToEndFact]
    public async Task TagsAndImpressions_ShouldFollowTheirContracts()
    {
        var world = await SeedAsync();
        using var client = _factory.CreateClient();

        using var tagsResponse = await client.GetAsync("/api/v1/tags");
        var tags = (await tagsResponse.Content.ReadFromJsonAsync<ApiResponse<List<TagDto>>>())!.Data!;
        tags.Select(tag => tag.TagType).Should().Equal("meal_time", "meal_time", "dietary", "category");
        tags.Take(2).Select(tag => tag.Name).Should().Equal("Bữa tối", "Bữa trưa");
        using var dietary = await client.GetAsync("/api/v1/tags?type=dietary");
        (await dietary.Content.ReadFromJsonAsync<ApiResponse<List<TagDto>>>())!.Data!.Should().ContainSingle();

        var body = new { requestId = Guid.NewGuid(), dishIds = new[] { world.Dish["bunBo"] } };
        using var anonymous = await client.PostAsJsonAsync("/api/v1/impressions", body);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var owner = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "RESTAURANT_OWNER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        using var asOwner = await client.PostAsJsonAsync("/api/v1/impressions", body);
        asOwner.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var traveler = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", traveler.AccessToken);
        using var accepted = await client.PostAsJsonAsync("/api/v1/impressions", body);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await accepted.Content.ReadFromJsonAsync<ApiResponse<RecordImpressionsResponseDto>>())!.Data!.Accepted.Should().BeTrue();
    }

    /// <summary>
    /// Restaurant A (rating 4.5, centre): comGa (like 50, dinner), bunBo (like 10, lunch + soup), bunCha (like 5, lunch +
    /// pork), plus a sold-out and a hidden dish. Restaurant B "Quán Riêu Ngon" (rating 4.0, 10 km north): bunRieu
    /// (like 100, lunch + soup). A closed and an unverified restaurant each have one dish. Only the four named dishes
    /// are candidates.
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
            void Set(long id, decimal rating, RestaurantVerificationStatus status, RestaurantOperatingStatus operating)
            {
                restaurants[id].RatingAvg = rating;
                restaurants[id].VerificationStatus = status;
                restaurants[id].OperatingStatus = operating;
            }

            Set(a, 4.5m, RestaurantVerificationStatus.Verified, RestaurantOperatingStatus.Open);
            Set(b, 4.0m, RestaurantVerificationStatus.Verified, RestaurantOperatingStatus.Open);
            Set(closed, 5.0m, RestaurantVerificationStatus.Verified, RestaurantOperatingStatus.TemporarilyClosed);
            Set(unverified, 5.0m, RestaurantVerificationStatus.Unverified, RestaurantOperatingStatus.Open);

            var tags = new Dictionary<string, Tag>
            {
                ["lunch"] = new() { TagType = TagType.MealTime, Code = "lunch", Name = "Bữa trưa" },
                ["dinner"] = new() { TagType = TagType.MealTime, Code = "dinner", Name = "Bữa tối" },
                ["soup"] = new() { TagType = TagType.Category, Code = "soup", Name = "Món nước" },
                ["pork"] = new() { TagType = TagType.Dietary, Code = "pork", Name = "Có thịt heo" }
            };
            Dish New(long restaurantId, string name, decimal price, DishForm form, int likes, params string[] tagCodes)
            {
                var dish = new Dish { RestaurantId = restaurantId, Name = name, Price = price, DishForm = form, LikeCount = likes };
                foreach (var code in tagCodes)
                {
                    dish.DishTags.Add(new DishTag { Tag = tags[code] });
                }

                return dish;
            }

            var dishes = new Dictionary<string, Dish>
            {
                ["comGa"] = New(a, "Cơm gà", 35000, DishForm.Dry, 50, "dinner"),
                ["bunBo"] = New(a, "Bún bò Huế", 45000, DishForm.Soup, 10, "lunch", "soup"),
                ["bunCha"] = New(a, "Bún chả", 40000, DishForm.Dry, 5, "lunch", "pork"),
                ["bunRieu"] = New(b, "Bún riêu", 30000, DishForm.Soup, 100, "lunch", "soup"),
                ["soldOut"] = New(a, "Bún mắm", 30000, DishForm.Soup, 999, "lunch"),
                ["hidden"] = New(a, "Bún ẩn", 30000, DishForm.Soup, 999, "lunch"),
                ["closed"] = New(closed, "Bún quán đóng", 30000, DishForm.Soup, 999, "lunch"),
                ["unverified"] = New(unverified, "Bún chưa duyệt", 30000, DishForm.Soup, 999, "lunch")
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

    private static async Task<BrowseDishesResponseDto> PageAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync($"/api/v1/dishes{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApiResponse<BrowseDishesResponseDto>>())!.Data!;
    }

    private static async Task<int[]> IdsAsync(HttpClient client, string query) =>
        (await PageAsync(client, query + (query.Length == 0 ? "?" : "&") + "limit=50")).Items.Select(item => item.Id).ToArray();

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("errorCode").GetString();
    }
}
