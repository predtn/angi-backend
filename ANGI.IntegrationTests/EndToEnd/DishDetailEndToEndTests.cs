using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Exercises DISH-02 through HTTP and PostgreSQL, above all which dishes are public.</summary>
public sealed class DishDetailEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private const string Password = "Password123";
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
        var (restaurantId, dishId, _) = await SeedPublicDishAsync();
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
        var (restaurantId, dishId, hiddenDishId) = await SeedPublicDishAsync();
        using var client = _factory.CreateClient();

        (await ErrorCodeAsync(client, dishId + 1000)).Should().Be("DISH_NOT_FOUND");
        (await ErrorCodeAsync(client, hiddenDishId)).Should().Be("DISH_NOT_FOUND");

        await UpdateRestaurantAsync(restaurantId, r => r.ModerationStatus = RestaurantModerationStatus.Hidden);
        (await ErrorCodeAsync(client, dishId)).Should().Be("DISH_NOT_FOUND");
        await UpdateRestaurantAsync(restaurantId, r => r.ModerationStatus = RestaurantModerationStatus.Visible);

        await UpdateRestaurantAsync(restaurantId, r => r.VerificationStatus = RestaurantVerificationStatus.Unverified);
        (await ErrorCodeAsync(client, dishId)).Should().Be("DISH_NOT_FOUND");
        await UpdateRestaurantAsync(restaurantId, r => r.VerificationStatus = RestaurantVerificationStatus.Verified);

        await UpdateRestaurantAsync(restaurantId, r => r.DeletedAt = DateTime.UtcNow);
        (await ErrorCodeAsync(client, dishId)).Should().Be("DISH_NOT_FOUND");
    }

    /// <summary>Creates a verified, visible restaurant with one public dish and one hidden dish.</summary>
    private async Task<(long RestaurantId, int DishId, int HiddenDishId)> SeedPublicDishAsync()
    {
        using var client = _factory.CreateClient();
        var email = $"dish-owner-{Guid.NewGuid():N}@integration.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterAccountRequestDto
        {
            Email = email,
            Password = Password,
            DisplayName = "Dish Owner",
            Role = "RESTAURANT_OWNER"
        })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequestDto
        {
            Token = _factory.Email.VerificationToken(email)
        })).EnsureSuccessStatusCode();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequestDto { Email = email, Password = Password });
        var auth = (await login.Content.ReadFromJsonAsync<ApiResponse<AuthResultDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
        using var created = await client.PostAsJsonAsync("/api/v1/owner/restaurant", new RegisterRestaurantRequestDto
        {
            Name = "Dish Detail Restaurant",
            Phone = "0900000000",
            AddressLine = "1 Integration Street",
            District = "District 1",
            ProvinceName = "Ho Chi Minh City",
            Latitude = 10.775m,
            Longitude = 106.700m
        });
        created.EnsureSuccessStatusCode();
        var restaurantId = (await created.Content.ReadFromJsonAsync<ApiResponse<OwnerRestaurantDto>>())!.Data!.Id;

        return await _factory.ExecuteDbAsync(async context =>
        {
            var restaurant = await context.Restaurants.SingleAsync(value => value.Id == restaurantId);
            restaurant.VerificationStatus = RestaurantVerificationStatus.Verified;
            MediaFile Media(string name) => new()
            {
                UploaderId = auth.User.Id,
                StorageKey = $"image/upload/dish/{name}-{Guid.NewGuid():N}.jpg",
                Url = $"https://cdn.test/dishes/{name}.jpg",
                MimeType = "image/jpeg"
            };
            var tag = new Tag { TagType = TagType.MealTime, Code = "lunch", Name = "Bữa trưa" };
            var dish = new Dish
            {
                RestaurantId = restaurantId,
                Name = "Bún bò Huế",
                Description = "Nước dùng hầm xương 8 tiếng, sả và mắm ruốc",
                Price = 45000,
                DishForm = DishForm.Soup,
                CoverMedia = Media("cover")
            };
            dish.DishTags.Add(new DishTag { Tag = tag });
            dish.Images.Add(new DishImage { SortOrder = 2, Media = Media("second") });
            dish.Images.Add(new DishImage { SortOrder = 1, Media = Media("first") });
            var hidden = new Dish
            {
                RestaurantId = restaurantId,
                Name = "Món đã ẩn",
                Price = 30000,
                Status = DishStatus.Hidden
            };
            context.Dishes.AddRange(dish, hidden);
            await context.SaveChangesAsync();
            return (restaurantId, dish.Id, hidden.Id);
        });
    }

    private Task UpdateRestaurantAsync(long restaurantId, Action<Domain.Entities.Restaurant> change) =>
        _factory.ExecuteDbAsync(async context =>
        {
            var restaurant = await context.Restaurants.IgnoreQueryFilters().SingleAsync(value => value.Id == restaurantId);
            change(restaurant);
            await context.SaveChangesAsync();
        });

    private static async Task<string?> ErrorCodeAsync(HttpClient client, int dishId)
    {
        using var response = await client.GetAsync($"/api/v1/dishes/{dishId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("errorCode").GetString();
    }
}
