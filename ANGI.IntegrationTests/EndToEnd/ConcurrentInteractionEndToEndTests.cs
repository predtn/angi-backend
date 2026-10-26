using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>
/// Sends simultaneous requests through the real pipeline and PostgreSQL to check that counters and one-per-user rows
/// stay exact: BLOG-08 / BLOG-09 likes, BLOG-10 / BLOG-11 comments and OWN-20 replies.
/// </summary>
public sealed class ConcurrentInteractionEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public ConcurrentInteractionEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Simultaneous likes and unlikes, including repeats by one traveler, keep like_count equal to the rows.
    /// <summary>Verifies BLOG-08 and BLOG-09 never lose or double a count under concurrency.</summary>
    [PostgresEndToEndFact]
    public async Task LikeAndUnlike_Concurrently_ShouldKeepLikeCountExact()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var travelers = await TravelersAsync(client, 8);
        var blogId = await SeedBlogAsync(travelers[0].UserId);
        var url = $"/api/v1/blogs/{blogId}/like";

        var likes = travelers.Select(traveler => SendAsync(client, HttpMethod.Put, url, traveler.Token))
            .Concat(Enumerable.Range(0, 3).Select(_ => SendAsync(client, HttpMethod.Put, url, travelers[1].Token)));
        foreach (var response in await Task.WhenAll(likes))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        (await CountsAsync(blogId)).Should().Be((8, 8));

        var unlikes = travelers.Take(4).Select(traveler => SendAsync(client, HttpMethod.Delete, url, traveler.Token))
            .Concat(Enumerable.Range(0, 3).Select(_ => SendAsync(client, HttpMethod.Delete, url, travelers[0].Token)));
        foreach (var response in await Task.WhenAll(unlikes))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        (await CountsAsync(blogId)).Should().Be((4, 4));
    }

    // TEST-02: Simultaneous comments all count, and deleting one comment twice at once decrements once.
    /// <summary>Verifies BLOG-10 and BLOG-11 keep comment_count equal to the visible comments.</summary>
    [PostgresEndToEndFact]
    public async Task CommentAndDelete_Concurrently_ShouldKeepCommentCountExact()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var travelers = await TravelersAsync(client, 6);
        var blogId = await SeedBlogAsync(travelers[0].UserId);
        var url = $"/api/v1/blogs/{blogId}/comments";

        var created = await Task.WhenAll(travelers.Select(traveler =>
            SendAsync(client, HttpMethod.Post, url, traveler.Token, new { content = "Quán này ngon thật!" })));
        foreach (var response in created)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        }

        (await CommentCountsAsync(blogId)).Should().Be((6, 6));

        var commentId = await _factory.ExecuteDbAsync(context => context.BlogComments
            .Where(comment => comment.UserId == travelers[2].UserId).Select(comment => comment.Id).SingleAsync());
        var deletes = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            SendAsync(client, HttpMethod.Delete, $"{url}/{commentId}", travelers[2].Token)));

        deletes.Should().Contain(response => response.IsSuccessStatusCode);
        deletes.Should().OnlyContain(response => response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
        (await CommentCountsAsync(blogId)).Should().Be((5, 5));
    }

    // TEST-03: Two simultaneous first replies to one review both succeed and leave exactly one reply.
    /// <summary>Verifies OWN-20 locks the review instead of failing on the one-reply-per-review index.</summary>
    [PostgresEndToEndFact]
    public async Task ReplyReview_Concurrently_ShouldKeepOneReply()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var owner = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "RESTAURANT_OWNER");
        using var restaurant = await SendAsync(client, HttpMethod.Post, "/api/v1/owner/restaurant", owner.AccessToken, new RegisterRestaurantRequestDto
        {
            Name = "Quán Trả Lời",
            Phone = "0900000000",
            AddressLine = "1 Integration Street",
            District = "District 1",
            ProvinceName = "Ho Chi Minh City",
            Latitude = 10.775m,
            Longitude = 106.700m
        });
        restaurant.EnsureSuccessStatusCode();
        var restaurantId = (await restaurant.Content.ReadFromJsonAsync<ApiResponse<OwnerRestaurantDto>>())!.Data!.Id;
        await _factory.ExecuteDbAsync(context => context.Restaurants.Where(value => value.Id == restaurantId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.VerificationStatus, RestaurantVerificationStatus.Verified)));
        var traveler = (await TravelersAsync(client, 1))[0];
        using var review = await SendAsync(client, HttpMethod.Post, $"/api/v1/restaurants/{restaurantId}/reviews", traveler.Token,
            new { rating = 5, content = "Ngon" });
        review.StatusCode.Should().Be(HttpStatusCode.Created, await review.Content.ReadAsStringAsync());
        var reviewId = await _factory.ExecuteDbAsync(context => context.RestaurantReviews.Select(value => value.Id).SingleAsync());

        var replies = await Task.WhenAll(Enumerable.Range(0, 3).Select(index => SendAsync(client, HttpMethod.Put,
            $"/api/v1/owner/restaurant/reviews/{reviewId}/reply", owner.AccessToken, new { content = $"Cảm ơn bạn {index}" })));

        foreach (var response in replies)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        (await _factory.ExecuteDbAsync(context => context.ReviewReplies.CountAsync(value => value.ReviewId == reviewId))).Should().Be(1);
        (await _factory.ExecuteDbAsync(context => context.Notifications.CountAsync(value => value.Type == "REVIEW_REPLIED"))).Should().Be(1);
    }

    private sealed record Traveler(int UserId, string Token);

    private async Task<Traveler[]> TravelersAsync(HttpClient client, int count)
    {
        var travelers = new Traveler[count];
        for (var index = 0; index < count; index++)
        {
            var auth = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
            travelers[index] = new Traveler(auth.User.Id, auth.AccessToken);
        }

        return travelers;
    }

    private Task<long> SeedBlogAsync(int authorId) => _factory.ExecuteDbAsync(async context =>
    {
        var blog = new Blog
        {
            AuthorId = authorId,
            Title = "Bài đông người",
            Slug = $"bai-dong-nguoi-{Guid.NewGuid():N}",
            Content = "Nội dung",
            PublishedAt = DateTime.UtcNow
        };
        context.Blogs.Add(blog);
        await context.SaveChangesAsync();
        return blog.Id;
    });

    private Task<(int Count, int Rows)> CountsAsync(long blogId) => _factory.ExecuteDbAsync(async context => (
        await context.Blogs.Where(blog => blog.Id == blogId).Select(blog => blog.LikeCount).SingleAsync(),
        await context.BlogLikes.CountAsync(like => like.BlogId == blogId)));

    private Task<(int Count, int Rows)> CommentCountsAsync(long blogId) => _factory.ExecuteDbAsync(async context => (
        await context.Blogs.Where(blog => blog.Id == blogId).Select(blog => blog.CommentCount).SingleAsync(),
        await context.BlogComments.CountAsync(comment => comment.BlogId == blogId)));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
