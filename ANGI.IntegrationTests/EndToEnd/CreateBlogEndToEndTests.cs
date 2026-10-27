using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.DTOs.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Runs BLOG-04 through the real HTTP pipeline, use case, repositories, EF Core, and PostgreSQL.</summary>
public sealed class CreateBlogEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes BLOG-04 end-to-end tests with the dedicated PostgreSQL fixture.</summary>
    public CreateBlogEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Persist sanitized content, ordered images, and an immutable complete roadmap snapshot through BLOG-04.
    /// <summary>Verifies the principal share-roadmap workflow and its persisted response contract end to end.</summary>
    [PostgresEndToEndFact]
    public async Task Create_WithOwnedRoadmapAndMedia_ShouldPersistImmutableSnapshot()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        var seed = await SeedOwnedResourcesAsync(auth.User.Id);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        using var response = await client.PostAsJsonAsync("/api/v1/blogs", new CreateBlogRequestDto
        {
            Title = "Hành trình Đà Nẵng",
            Content = "<p onclick=\"bad()\">Ngày đầu tiên</p><script>alert(1)</script>",
            CoverMediaId = seed.CoverId,
            ImageMediaIds = [seed.SecondImageId, seed.FirstImageId],
            RoadmapId = seed.RoadmapId
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var detail = (await response.Content.ReadFromJsonAsync<ApiResponse<BlogDetailDto>>())!.Data!;
        detail.Title.Should().Be("Hành trình Đà Nẵng");
        detail.Slug.Should().Be("hanh-trinh-da-nang");
        detail.Content.Should().Contain("Ngày đầu tiên");
        detail.Content.ToLowerInvariant().Should().NotContain("onclick").And.NotContain("script");
        detail.CoverUrl.Should().Be("https://cdn.test/blog-cover.jpg");
        detail.Images.Should().Equal("https://cdn.test/blog-second.jpg", "https://cdn.test/blog-first.jpg");
        detail.IsMine.Should().BeTrue();
        detail.LikedByMe.Should().BeFalse();
        detail.RoadmapSnapshot!.TotalCost.Should().Be(45000);
        detail.RoadmapSnapshot.Days.Should().HaveCount(2);
        detail.RoadmapSnapshot.Days[0].Items.Should().ContainSingle();
        detail.RoadmapSnapshot.Days[0].Items[0].RestaurantName.Should().Be("Quán snapshot");

        await _factory.ExecuteDbAsync(async context =>
        {
            var dish = await context.Dishes.SingleAsync(item => item.Id == seed.DishId);
            var restaurant = await context.Restaurants.SingleAsync(item => item.Id == seed.RestaurantId);
            dish.Price = 99000;
            restaurant.Name = "Quán đã đổi tên";
            await context.SaveChangesAsync();
        });

        await _factory.ExecuteDbAsync(async context =>
        {
            var blog = await context.Blogs.AsNoTracking().SingleAsync(item => item.Id == detail.Id);
            blog.Status.Should().Be(BlogStatus.Published);
            blog.RoadmapId.Should().Be(seed.RoadmapId);
            using var snapshot = JsonDocument.Parse(blog.RoadmapSnapshot!);
            var item = snapshot.RootElement.GetProperty("days")[0].GetProperty("items")[0];
            item.GetProperty("price").GetDecimal().Should().Be(45000);
            item.GetProperty("restaurantName").GetString().Should().Be("Quán snapshot");
            var imageOrder = await context.BlogImages.AsNoTracking()
                .Where(image => image.BlogId == blog.Id)
                .OrderBy(image => image.SortOrder)
                .Select(image => image.MediaId)
                .ToArrayAsync();
            imageOrder.Should().Equal(seed.SecondImageId, seed.FirstImageId);
        });
    }

    // TEST-02: Create a plain blog and generate a bounded suffix when its readable slug already exists.
    /// <summary>Verifies nullable request regions and real unique-slug persistence through PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task Create_WithoutOptionalReferencesAndWithSlugCollision_ShouldReturnPlainSuffixedBlog()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        await _factory.ExecuteDbAsync(async context =>
        {
            context.Blogs.Add(new Blog
            {
                AuthorId = auth.User.Id,
                Title = "Bài trùng",
                Slug = "bai-trung",
                Content = "Bài cũ",
                PublishedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        using var response = await client.PostAsJsonAsync("/api/v1/blogs", new CreateBlogRequestDto
        {
            Title = "Bài trùng",
            Content = "Nội dung mới"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var detail = (await response.Content.ReadFromJsonAsync<ApiResponse<BlogDetailDto>>())!.Data!;
        detail.Slug.Should().Be("bai-trung-2");
        detail.CoverUrl.Should().BeNull();
        detail.Images.Should().BeEmpty();
        detail.HasRoadmap.Should().BeFalse();
        detail.RoadmapId.Should().BeNull();
        detail.RoadmapSnapshot.Should().BeNull();
    }

    // TEST-03: Reject a foreign roadmap, foreign media, and the first image count above the documented maximum.
    /// <summary>Verifies every BLOG-04 ownership and collection boundary through HTTP and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task Create_WithForeignResourcesOrTooManyImages_ShouldReturnDocumentedErrors()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var traveler = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        var other = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        var foreign = await SeedOwnedResourcesAsync(other.User.Id);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", traveler.AccessToken);

        using var roadmapResponse = await client.PostAsJsonAsync("/api/v1/blogs", new CreateBlogRequestDto
        {
            Title = "Không được chia sẻ",
            Content = "Nội dung",
            RoadmapId = foreign.RoadmapId
        });
        using var mediaResponse = await client.PostAsJsonAsync("/api/v1/blogs", new CreateBlogRequestDto
        {
            Title = "Không được dùng ảnh",
            Content = "Nội dung",
            CoverMediaId = foreign.CoverId
        });
        using var countResponse = await client.PostAsJsonAsync("/api/v1/blogs", new CreateBlogRequestDto
        {
            Title = "Quá nhiều ảnh",
            Content = "Nội dung",
            ImageMediaIds = Enumerable.Range(1, 21).Select(value => (long)value).ToArray()
        });

        roadmapResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await roadmapResponse.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        mediaResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await mediaResponse.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("VALIDATION_FAILED");
        countResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await countResponse.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("VALIDATION_FAILED");
        (await _factory.ExecuteDbAsync(context => context.Blogs.CountAsync())).Should().Be(0);
    }

    /// <summary>Creates media, a restaurant dish, and a two-day roadmap owned by one traveler.</summary>
    private Task<BlogSeed> SeedOwnedResourcesAsync(int userId) =>
        _factory.ExecuteDbAsync(async context =>
        {
            MediaFile Media(string name) => new()
            {
                UploaderId = userId,
                StorageKey = $"image/upload/blog/{name}-{Guid.NewGuid():N}.jpg",
                Url = $"https://cdn.test/blog-{name}.jpg",
                MimeType = "image/jpeg"
            };
            var cover = Media("cover");
            var first = Media("first");
            var second = Media("second");
            context.MediaFiles.AddRange(cover, first, second);
            var restaurant = new ANGI.Domain.Entities.Restaurant
            {
                OwnerId = userId,
                Name = "Quán snapshot",
                Slug = $"snapshot-{Guid.NewGuid():N}",
                AddressLine = "12 Lê Duẩn",
                ProvinceName = "Đà Nẵng",
                Latitude = 16.0678m,
                Longitude = 108.2208m
            };
            context.Restaurants.Add(restaurant);
            await context.SaveChangesAsync();
            var dish = new Dish
            {
                RestaurantId = restaurant.Id,
                Name = "Bún bò Huế",
                Price = 45000
            };
            context.Dishes.Add(dish);
            await context.SaveChangesAsync();
            var roadmap = new Roadmap
            {
                UserId = userId,
                Title = "Hai ngày ăn ngon",
                StartDate = new DateOnly(2026, 10, 20),
                NumDays = 2,
                Days =
                [
                    new RoadmapDay
                    {
                        DayNumber = 1,
                        Items =
                        [
                            new RoadmapItem
                            {
                                DishId = dish.Id,
                                MealSlot = MealSlot.Breakfast,
                                SortOrder = 0,
                                Source = RoadmapItemSource.Manual
                            }
                        ]
                    },
                    new RoadmapDay { DayNumber = 2 }
                ]
            };
            context.Roadmaps.Add(roadmap);
            await context.SaveChangesAsync();
            return new BlogSeed(
                cover.Id,
                first.Id,
                second.Id,
                restaurant.Id,
                dish.Id,
                roadmap.Id);
        });

    /// <summary>Identifiers of all PostgreSQL records required by a BLOG-04 scenario.</summary>
    private sealed record BlogSeed(
        long CoverId,
        long FirstImageId,
        long SecondImageId,
        long RestaurantId,
        int DishId,
        long RoadmapId);
}
