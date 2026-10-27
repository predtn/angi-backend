using System.Net.Http.Headers;
using System.Net.Http.Json;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.WebApi.Common.Models;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.Fixtures;

/// <summary>Seeds public dishes for the Discovery end-to-end tests (dishes exist only after menu approval).</summary>
public static class DiscoverySeed
{
    public const string Password = "Password123";

    /// <summary>Ids of a verified, visible restaurant with one public dish and one hidden dish.</summary>
    public sealed record PublicDish(long RestaurantId, int DishId, int HiddenDishId);

    /// <summary>
    /// Registers an owner and a restaurant through HTTP, then marks the restaurant verified and adds a public dish
    /// (cover, two ordered images, one tag) and a hidden dish directly in PostgreSQL.
    /// </summary>
    public static async Task<PublicDish> PublicDishAsync(PostgresApiFactory factory)
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAndLoginAsync(factory, client, "RESTAURANT_OWNER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        using var created = await client.PostAsJsonAsync("/api/v1/owner/restaurant", new RegisterRestaurantRequestDto
        {
            Name = "Dish Detail Restaurant",
            Phone = "0900000000",
            AddressLine = "1 Integration Street",
            ProvinceName = "Ho Chi Minh City",
            Latitude = 10.775m,
            Longitude = 106.700m
        });
        created.EnsureSuccessStatusCode();
        var restaurantId = (await created.Content.ReadFromJsonAsync<ApiResponse<OwnerRestaurantDto>>())!.Data!.Id;

        return await factory.ExecuteDbAsync(async context =>
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
            var dish = new Dish
            {
                RestaurantId = restaurantId,
                Name = "Bún bò Huế",
                Description = "Nước dùng hầm xương 8 tiếng, sả và mắm ruốc",
                Price = 45000,
                DishForm = DishForm.Soup,
                CoverMedia = Media("cover")
            };
            dish.DishTags.Add(new DishTag { Tag = new Tag { TagType = TagType.MealTime, Code = "lunch", Name = "Bữa trưa" } });
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
            return new PublicDish(restaurantId, dish.Id, hidden.Id);
        });
    }

    /// <summary>Registers, verifies and logs in an account of the given role through the public auth endpoints.</summary>
    public static async Task<AuthResultDto> RegisterAndLoginAsync(PostgresApiFactory factory, HttpClient client, string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@integration.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterAccountRequestDto
        {
            Email = email,
            Password = Password,
            DisplayName = "Discovery User",
            Role = role
        })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequestDto
        {
            Token = factory.Email.VerificationToken(email)
        })).EnsureSuccessStatusCode();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequestDto { Email = email, Password = Password });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<ApiResponse<AuthResultDto>>())!.Data!;
    }

    /// <summary>Changes the seeded restaurant directly, deleted rows included.</summary>
    public static Task UpdateRestaurantAsync(
        PostgresApiFactory factory,
        long restaurantId,
        Action<Domain.Entities.Restaurant> change) =>
        factory.ExecuteDbAsync(async context =>
        {
            var restaurant = await context.Restaurants.IgnoreQueryFilters().SingleAsync(value => value.Id == restaurantId);
            change(restaurant);
            await context.SaveChangesAsync();
        });
}
