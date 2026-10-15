using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>HOME-01 through HTTP and PostgreSQL; the test host's reco keeps the candidate order.</summary>
public sealed class HomeEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    public HomeEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Trending puts recent likes first and completes with like_count; top restaurants follow rating;
    // province filters dishes and restaurants; blogs of the last 30 days come first; recommended is ranked.
    [PostgresEndToEndFact]
    public async Task Home_ShouldBuildEveryBlockFromTheDatabase()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await SeedAsync();
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/home?province=đà nẵng&limit=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var home = (await response.Content.ReadFromJsonAsync<ApiResponse<GetHomeResponseDto>>())!.Data!;
        // recentLike has 2 likes this week; oldLike's like is 10 days old, so like_count decides after it.
        home.TrendingDishes.Select(dish => dish.Id).Should().Equal(seed["recentLike"], seed["popular"], seed["oldLike"]);
        home.TrendingDishes.Should().OnlyContain(dish => dish.Rank == null);
        home.TopRestaurants.Select(restaurant => restaurant.Name).Should().Equal("Quán A", "Quán B");
        home.FeaturedBlogs.Select(blog => blog.Title).Should().Equal("Blog mới ít like", "Blog cũ nhiều like");
        home.FeaturedBlogs[0].Excerpt.Should().HaveLength(200);
        home.Recommended.RequestId.Should().BeNull();
        home.Recommended.Items.Select(dish => dish.Rank).Should().Equal(1, 2, 3);
        home.Recommended.Items.Should().NotContain(dish => dish.Id == seed["hue"]);
    }

    // TEST-02: Without province every public dish competes; an invalid limit is 400.
    [PostgresEndToEndFact]
    public async Task Home_WithoutProvinceAndWithBadLimit_ShouldFollowTheContract()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await SeedAsync();
        using var client = _factory.CreateClient();

        var home = (await client.GetFromJsonAsync<ApiResponse<GetHomeResponseDto>>("/api/v1/home"))!.Data!;
        home.TrendingDishes.Select(dish => dish.Id).Should().Contain(seed["hue"]);

        using var bad = await client.GetAsync("/api/v1/home?limit=51");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<Dictionary<string, int>> SeedAsync()
    {
        var a = await RestaurantAsync("Quán A", "Đà Nẵng");
        var b = await RestaurantAsync("Quán B", "Đà Nẵng");
        var hue = await RestaurantAsync("Quán Huế", "Thừa Thiên Huế");

        return await _factory.ExecuteDbAsync(async context =>
        {
            var restaurants = await context.Restaurants.ToDictionaryAsync(r => r.Id);
            restaurants[a].RatingAvg = 4.8m;
            restaurants[b].RatingAvg = 4.1m;
            restaurants[hue].RatingAvg = 5.0m;
            foreach (var restaurant in restaurants.Values)
            {
                restaurant.VerificationStatus = RestaurantVerificationStatus.Verified;
            }

            var traveler = new User
            {
                Email = $"home-{Guid.NewGuid():N}@integration.test",
                RoleId = 1,
                DisplayName = "Home Traveler",
                Status = UserStatus.Active
            };
            Dish New(long restaurantId, string name, int likes) =>
                new() { RestaurantId = restaurantId, Name = name, Price = 30000, LikeCount = likes };
            var dishes = new Dictionary<string, Dish>
            {
                ["recentLike"] = New(b, "Món like tuần này", 1),
                ["popular"] = New(a, "Món nhiều like", 90),
                ["oldLike"] = New(a, "Món like cũ", 20),
                ["hue"] = New(hue, "Bún bò Huế", 500)
            };
            context.Dishes.AddRange(dishes.Values);
            await context.SaveChangesAsync();

            context.Users.Add(traveler);
            await context.SaveChangesAsync();
            DishFeedback Like(int dishId, int daysAgo) => new()
            {
                EventUuid = Guid.NewGuid(),
                UserId = traveler.Id,
                DishId = dishId,
                Value = FeedbackValue.Like,
                CreatedAt = DateTime.UtcNow.AddDays(-daysAgo)
            };
            context.DishFeedbacks.AddRange(
                Like(dishes["recentLike"].Id, 1),
                Like(dishes["recentLike"].Id, 2),
                Like(dishes["oldLike"].Id, 10));

            context.Blogs.AddRange(
                new Blog
                {
                    AuthorId = traveler.Id, Title = "Blog cũ nhiều like", Slug = $"old-{Guid.NewGuid():N}",
                    Content = "Bài cũ", LikeCount = 500, PublishedAt = DateTime.UtcNow.AddDays(-60)
                },
                new Blog
                {
                    AuthorId = traveler.Id, Title = "Blog mới ít like", Slug = $"new-{Guid.NewGuid():N}",
                    Content = new string('x', 300), LikeCount = 1, PublishedAt = DateTime.UtcNow.AddDays(-2)
                },
                new Blog
                {
                    AuthorId = traveler.Id, Title = "Blog bị ẩn", Slug = $"hidden-{Guid.NewGuid():N}",
                    Content = "Ẩn", LikeCount = 999, PublishedAt = DateTime.UtcNow, Status = BlogStatus.Hidden
                });
            await context.SaveChangesAsync();
            return dishes.ToDictionary(pair => pair.Key, pair => pair.Value.Id);
        });
    }

    private async Task<long> RestaurantAsync(string name, string province)
    {
        using var client = _factory.CreateClient();
        var owner = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "RESTAURANT_OWNER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        using var response = await client.PostAsJsonAsync("/api/v1/owner/restaurant", new RegisterRestaurantRequestDto
        {
            Name = name,
            Phone = "0900000000",
            AddressLine = "1 Integration Street",
            District = "Hải Châu",
            ProvinceName = province,
            Latitude = 16.0678m,
            Longitude = 108.2208m
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<OwnerRestaurantDto>>())!.Data!.Id;
    }
}
