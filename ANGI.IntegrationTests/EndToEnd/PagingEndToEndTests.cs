using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Checks page-based lists through the real pipeline and PostgreSQL at the largest page a client can send.</summary>
public sealed class PagingEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public PagingEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: page = int.MaxValue returns an empty page instead of a negative OFFSET error.
    /// <summary>Verifies REST-01, BLOG-01, REST-04, NOTI-01 and RM-02 with the widest accepted page.</summary>
    [PostgresEndToEndFact]
    public async Task Lists_WithLargestPage_ShouldReturnEmptyPage()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var traveler = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", traveler.AccessToken);
        var page = int.MaxValue;

        foreach (var url in new[]
                 {
                     $"/api/v1/restaurants?page={page}&pageSize=100",
                     $"/api/v1/blogs?page={page}&pageSize=100",
                     $"/api/v1/restaurants/{seed.RestaurantId}/reviews?page={page}&pageSize=100",
                     $"/api/v1/me/notifications?page={page}&pageSize=100",
                     $"/api/v1/roadmaps?page={page}&pageSize=100"
                 })
        {
            using var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{url} answered {body}");
            using var document = JsonDocument.Parse(body);
            document.RootElement.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(0, url);
        }
    }
}
