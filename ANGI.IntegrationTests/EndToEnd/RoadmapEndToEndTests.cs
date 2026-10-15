using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Exercises ANGI-104 through the real HTTP pipeline, EF Core repository, and PostgreSQL.</summary>
public sealed class RoadmapEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private const string Password = "Password123";
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public RoadmapEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Create a pinned roadmap, shrink it, and switch to gps through HTTP and PostgreSQL.
    /// <summary>Verifies RM-01 and RM-04 persist days and maintain the origin invariant end to end.</summary>
    [PostgresEndToEndFact]
    public async Task CreateAndUpdateRoadmap_ShouldPersistDaysAndOriginInvariant()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterTravelerAndLoginAsync(client, "roadmap-owner@integration.test");
        Authorize(client, auth.AccessToken);

        using var createResponse = await client.PostAsJsonAsync("/api/v1/roadmaps", new CreateRoadmapRequestDto
        {
            Title = "Integration trip",
            StartDate = new DateOnly(2026, 10, 20),
            NumDays = 3,
            BudgetAmount = 1_500_000,
            OriginMode = "pinned",
            OriginLatitude = 10.775m,
            OriginLongitude = 106.700m,
            OriginLabel = "Integration Hotel"
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());
        var created = await SuccessDataAsync<RoadmapDetailDto>(createResponse);
        created.Itinerary.Select(day => day.DayNumber).Should().Equal(1, 2, 3);

        using var updateResponse = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{created.Overview.Id}", new
        {
            title = "Updated integration trip",
            numDays = 2,
            originMode = "gps"
        });

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK, await updateResponse.Content.ReadAsStringAsync());
        var updated = await SuccessDataAsync<RoadmapDetailDto>(updateResponse);
        updated.Overview.Title.Should().Be("Updated integration trip");
        updated.Overview.OriginMode.Should().Be("gps");
        updated.Overview.OriginLatitude.Should().BeNull();
        updated.Itinerary.Select(day => day.DayNumber).Should().Equal(1, 2);
        await _factory.ExecuteDbAsync(async context =>
        {
            var persisted = await context.Roadmaps
                .Include(roadmap => roadmap.Days)
                .SingleAsync(roadmap => roadmap.Id == created.Overview.Id);
            persisted.UserId.Should().Be(auth.User.Id);
            persisted.NumDays.Should().Be(2);
            persisted.Days.Select(day => day.DayNumber).Order().Should().Equal(1, 2);
            persisted.OriginMode.Should().Be(RoadmapOriginMode.Gps);
            persisted.OriginLatitude.Should().BeNull();
            persisted.OriginLongitude.Should().BeNull();
            persisted.OriginLabel.Should().BeNull();
        });
    }

    // TEST-02: Hide another traveler's roadmap behind ROADMAP_NOT_FOUND.
    /// <summary>Verifies ownership filtering survives the complete HTTP and database boundary.</summary>
    [PostgresEndToEndFact]
    public async Task UpdateAnotherTravelersRoadmap_ShouldReturnNotFound()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var first = await RegisterTravelerAndLoginAsync(client, "first-roadmap@integration.test");
        Authorize(client, first.AccessToken);
        using var createResponse = await client.PostAsJsonAsync("/api/v1/roadmaps", new CreateRoadmapRequestDto
        {
            Title = "Private trip",
            NumDays = 1
        });
        var created = await SuccessDataAsync<RoadmapDetailDto>(createResponse);
        var second = await RegisterTravelerAndLoginAsync(client, "second-roadmap@integration.test");
        Authorize(client, second.AccessToken);

        using var response = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{created.Overview.Id}", new
        {
            title = "Unauthorized change"
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        error!.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
    }

    /// <summary>Registers, verifies, and logs in a traveler through public HTTP endpoints.</summary>
    private async Task<AuthResultDto> RegisterTravelerAndLoginAsync(HttpClient client, string email)
    {
        client.DefaultRequestHeaders.Authorization = null;
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterAccountRequestDto
        {
            Email = email,
            Password = Password,
            DisplayName = "Integration Traveler",
            Role = "TRAVELER"
        });
        register.EnsureSuccessStatusCode();
        using var verify = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequestDto
        {
            Token = _factory.Email.VerificationToken(email)
        });
        verify.EnsureSuccessStatusCode();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequestDto
        {
            Email = email,
            Password = Password
        });
        login.EnsureSuccessStatusCode();
        return await SuccessDataAsync<AuthResultDto>(login);
    }

    /// <summary>Adds the issued bearer token to subsequent traveler requests.</summary>
    private static void Authorize(HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    /// <summary>Reads a successful API envelope and returns its required data payload.</summary>
    private static async Task<T> SuccessDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }
}
