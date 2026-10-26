using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Domain.Entities;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Exercises REP-01 through the real HTTP pipeline, repositories and PostgreSQL.</summary>
public sealed class ReportEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public ReportEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Evidence may only reference the reporter's own uploads: another user's private file is refused.
    /// <summary>Verifies the REP-01 evidence rule so a Mod never receives a signed URL of an unrelated private file.</summary>
    [PostgresEndToEndFact]
    public async Task ReportRestaurant_WithForeignEvidence_ShouldRejectAndKeepOwnEvidence()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var traveler = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", traveler.AccessToken);
        var (ownMediaId, foreignMediaId) = await _factory.ExecuteDbAsync(async context =>
        {
            var ownerId = await context.Restaurants.Where(value => value.Id == seed.RestaurantId).Select(value => value.OwnerId).SingleAsync();
            var own = Media(traveler.User.Id, "image/upload/report_evidence/own.jpg");
            var foreign = Media(ownerId, "raw/authenticated/verification_doc/license.pdf");
            context.MediaFiles.AddRange(own, foreign);
            await context.SaveChangesAsync();
            return (own.Id, foreign.Id);
        });
        var url = $"/api/v1/restaurants/{seed.RestaurantId}/reports";

        using var foreignReport = await client.PostAsJsonAsync(url, new { reasonCode = "scam", evidenceMediaIds = new[] { ownMediaId, foreignMediaId } });
        using var ownReport = await client.PostAsJsonAsync(url, new { reasonCode = "scam", evidenceMediaIds = new[] { ownMediaId, ownMediaId } });

        foreignReport.StatusCode.Should().Be(HttpStatusCode.BadRequest, await foreignReport.Content.ReadAsStringAsync());
        (await foreignReport.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("VALIDATION_FAILED");
        ownReport.StatusCode.Should().Be(HttpStatusCode.Created, await ownReport.Content.ReadAsStringAsync());
        await _factory.ExecuteDbAsync(async context =>
        {
            var report = await context.Reports.SingleAsync();
            JsonSerializer.Deserialize<long[]>(report.Evidence).Should().Equal(ownMediaId);
        });
    }

    // TEST-02: A restaurant travelers cannot see can be neither reviewed nor reported; a visible one gets an exact average.
    /// <summary>Verifies the REV-01 / REP-01 visibility rule and the rating recomputed from visible reviews.</summary>
    [PostgresEndToEndFact]
    public async Task ReviewAndReport_ShouldNeedAVisibleRestaurantAndRecomputeRating()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(_factory);
        using var client = _factory.CreateClient();
        var first = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        var second = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        var reviews = $"/api/v1/restaurants/{seed.RestaurantId}/reviews";
        var reports = $"/api/v1/restaurants/{seed.RestaurantId}/reports";
        await _factory.ExecuteDbAsync(context => context.Restaurants.Where(value => value.Id == seed.RestaurantId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.ModerationStatus, Domain.Enums.RestaurantModerationStatus.Hidden)));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.AccessToken);
        using var hiddenReview = await client.PostAsJsonAsync(reviews, new { rating = 4 });
        using var hiddenReport = await client.PostAsJsonAsync(reports, new { reasonCode = "spam" });

        hiddenReview.StatusCode.Should().Be(HttpStatusCode.NotFound, await hiddenReview.Content.ReadAsStringAsync());
        (await hiddenReview.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
        hiddenReport.StatusCode.Should().Be(HttpStatusCode.NotFound, await hiddenReport.Content.ReadAsStringAsync());
        (await hiddenReport.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");

        await _factory.ExecuteDbAsync(context => context.Restaurants.Where(value => value.Id == seed.RestaurantId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.ModerationStatus, Domain.Enums.RestaurantModerationStatus.Visible)));
        using var firstReview = await client.PostAsJsonAsync(reviews, new { rating = 4 });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        using var secondReview = await client.PostAsJsonAsync(reviews, new { rating = 5 });

        firstReview.StatusCode.Should().Be(HttpStatusCode.Created, await firstReview.Content.ReadAsStringAsync());
        secondReview.StatusCode.Should().Be(HttpStatusCode.Created, await secondReview.Content.ReadAsStringAsync());
        var rating = await _factory.ExecuteDbAsync(context => context.Restaurants.Where(value => value.Id == seed.RestaurantId)
            .Select(value => new { value.RatingCount, value.RatingAvg }).SingleAsync());
        (rating.RatingCount, rating.RatingAvg).Should().Be((2, 4.50m));
    }

    private static MediaFile Media(int uploaderId, string storageKey) => new()
    {
        UploaderId = uploaderId,
        StorageKey = storageKey,
        Url = $"https://cdn.test/{storageKey}",
        MimeType = storageKey.EndsWith(".pdf") ? "application/pdf" : "image/jpeg"
    };
}
