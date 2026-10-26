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

    private static MediaFile Media(int uploaderId, string storageKey) => new()
    {
        UploaderId = uploaderId,
        StorageKey = storageKey,
        Url = $"https://cdn.test/{storageKey}",
        MimeType = storageKey.EndsWith(".pdf") ? "application/pdf" : "image/jpeg"
    };
}
