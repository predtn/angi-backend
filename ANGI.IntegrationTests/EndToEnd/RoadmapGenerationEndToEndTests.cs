using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>
/// Exercises RM-10 and RM-11 through the real HTTP pipeline, the hosted generation worker and PostgreSQL. The tests
/// host has no AI key, so every day is planned by the fallback.
/// </summary>
public sealed class RoadmapGenerationEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public RoadmapGenerationEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Fill the empty meals of a day, then replace one meal, and notify the traveler each time.
    /// <summary>Verifies the RM-10 job run, the RM-11 result, the items in RM-03 and the stored job and notification.</summary>
    [PostgresEndToEndFact]
    public async Task GenerateRoadmap_ShouldFillEmptyMealsThenReplaceSlotThroughWorker()
    {
        await _factory.ResetDatabaseAsync();
        var seed = await SeedDishesAsync();
        using var client = _factory.CreateClient();
        var traveler = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        Authorize(client, traveler.AccessToken);
        var roadmap = await CreatePinnedRoadmapAsync(client);
        var dayId = roadmap.Itinerary.Single().DayId;
        using var manual = await client.PostAsJsonAsync(
            $"/api/v1/roadmaps/{roadmap.Overview.Id}/days/{dayId}/items",
            new AddRoadmapItemRequestDto { DishId = seed.PublicDishId, MealSlot = "lunch" });
        manual.EnsureSuccessStatusCode();

        using var generate = await client.PostAsJsonAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}/generate", new GenerateRoadmapRequestDto
        {
            Scope = "day",
            DayId = dayId,
            Province = "Ho Chi Minh City",
            Note = "Ít cay"
        });

        generate.StatusCode.Should().Be(HttpStatusCode.Accepted, await generate.Content.ReadAsStringAsync());
        var queued = await SuccessDataAsync<GenerateRoadmapResponseDto>(generate);
        queued.Status.Should().Be("queued");
        var job = await WaitForJobAsync(client, roadmap.Overview.Id, queued.JobId);
        job.Status.Should().Be("succeeded");
        job.Scope.Should().Be("day");
        job.ErrorMessage.Should().BeNull();
        job.FinishedAt.Should().NotBeNull();
        job.CreatedItemIds.Should().HaveCount(2);

        var detail = await GetDetailAsync(client, roadmap.Overview.Id);
        var items = detail.Itinerary.Single().Items;
        items.Select(item => item.MealSlot).Should().BeEquivalentTo(["breakfast", "lunch", "dinner"]);
        var aiItems = items.Where(item => item.Source == "ai").ToArray();
        aiItems.Select(item => item.Id).Should().BeEquivalentTo(job.CreatedItemIds);
        aiItems.Select(item => item.MealSlot).Should().BeEquivalentTo(["breakfast", "dinner"]);
        aiItems.Select(item => item.Dish.Id).Should().NotContain(seed.PublicDishId).And.OnlyHaveUniqueItems();
        aiItems.Should().OnlyContain(item => item.SortOrder == 0 && item.RecoRequestId == null);
        await _factory.ExecuteDbAsync(async context =>
        {
            var stored = await context.RoadmapGenerationJobs.SingleAsync(value => value.Id == queued.JobId);
            stored.RequestedBy.Should().Be(traveler.User.Id);
            stored.Scope.Should().Be(GenerationScope.Day);
            stored.ModelName.Should().Be("fallback");
            stored.PromptVersion.Should().Be("rm10-v1");
            using var input = JsonDocument.Parse(stored.Input);
            input.RootElement.GetProperty("origin").GetProperty("mode").GetString().Should().Be("pinned");
            input.RootElement.GetProperty("radiusKm").GetDecimal().Should().Be(10);
            input.RootElement.GetProperty("radiusDropped").GetBoolean().Should().BeFalse();
            input.RootElement.GetProperty("candidateDishIds").EnumerateArray().Select(id => id.GetInt32())
                .Should().BeEquivalentTo(seed.ExtraDishIds);
            var notification = await context.Notifications.SingleAsync(value => value.RecipientId == traveler.User.Id);
            notification.Type.Should().Be("ROADMAP_GENERATED");
            notification.Body.Should().Be("AI đã thêm 2 món vào roadmap \"AI trip\".");
        });

        using var replace = await client.PostAsJsonAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}/generate", new GenerateRoadmapRequestDto
        {
            Scope = "slot",
            DayId = dayId,
            MealSlot = "lunch",
            ReplaceExisting = true,
            Province = "Ho Chi Minh City"
        });
        replace.StatusCode.Should().Be(HttpStatusCode.Accepted, await replace.Content.ReadAsStringAsync());
        var replaced = await WaitForJobAsync(client, roadmap.Overview.Id, (await SuccessDataAsync<GenerateRoadmapResponseDto>(replace)).JobId);

        replaced.Status.Should().Be("succeeded");
        var lunch = (await GetDetailAsync(client, roadmap.Overview.Id)).Itinerary.Single().Items
            .Where(item => item.MealSlot == "lunch").Should().ContainSingle().Subject;
        lunch.Source.Should().Be("ai");
        lunch.Id.Should().Be(replaced.CreatedItemIds.Single());
        // A replaced dish may come back: the public dish is the only one tagged lunch, so the fallback picks it again.
        lunch.Dish.Id.Should().Be(seed.PublicDishId);
        (await _factory.ExecuteDbAsync(context => context.Notifications.CountAsync(value => value.RecipientId == traveler.User.Id)))
            .Should().Be(2);
    }

    // TEST-02: Refuse an unknown day and a second active job, hide jobs of other travelers, and fail a job without candidates.
    /// <summary>Verifies the RM-10 / RM-11 errors and a failed job end to end.</summary>
    [PostgresEndToEndFact]
    public async Task GenerateRoadmap_WithInvalidTargets_ShouldReturnDocumentedErrorsAndFailedJob()
    {
        await _factory.ResetDatabaseAsync();
        await SeedDishesAsync();
        using var client = _factory.CreateClient();
        var owner = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        Authorize(client, owner.AccessToken);
        var roadmap = await CreatePinnedRoadmapAsync(client);
        var url = $"/api/v1/roadmaps/{roadmap.Overview.Id}/generate";

        using var missingDay = await client.PostAsJsonAsync(url, new GenerateRoadmapRequestDto { Scope = "day", DayId = 999999, Province = "Ho Chi Minh City" });
        await ShouldFailAsync(missingDay, HttpStatusCode.NotFound, "DAY_NOT_FOUND");

        // A running job is never claimed again by the worker, so it keeps the roadmap busy.
        var runningId = await _factory.ExecuteDbAsync(async context =>
        {
            var running = new RoadmapGenerationJob
            {
                RoadmapId = roadmap.Overview.Id,
                RequestedBy = owner.User.Id,
                Input = "{}",
                Status = GenerationJobStatus.Running
            };
            context.RoadmapGenerationJobs.Add(running);
            await context.SaveChangesAsync();
            return running.Id;
        });
        using var busy = await client.PostAsJsonAsync(url, new GenerateRoadmapRequestDto { Scope = "full", Province = "Ho Chi Minh City" });
        await ShouldFailAsync(busy, HttpStatusCode.Conflict, "GENERATION_IN_PROGRESS");
        await _factory.ExecuteDbAsync(async context =>
        {
            await context.RoadmapGenerationJobs.Where(value => value.Id == runningId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Status, GenerationJobStatus.Failed));
        });

        using var nothing = await client.PostAsJsonAsync(url, new GenerateRoadmapRequestDto { Scope = "full", Province = "Hà Nội" });
        nothing.StatusCode.Should().Be(HttpStatusCode.Accepted, await nothing.Content.ReadAsStringAsync());
        var jobId = (await SuccessDataAsync<GenerateRoadmapResponseDto>(nothing)).JobId;
        var failed = await WaitForJobAsync(client, roadmap.Overview.Id, jobId);
        failed.Status.Should().Be("failed");
        failed.ErrorMessage.Should().Be("Không tìm thấy món phù hợp với các điều kiện đã chọn.");
        failed.CreatedItemIds.Should().BeEmpty();
        failed.FinishedAt.Should().NotBeNull();
        (await GetDetailAsync(client, roadmap.Overview.Id)).Itinerary.Single().Items.Should().BeEmpty();
        (await _factory.ExecuteDbAsync(context => context.Notifications.CountAsync())).Should().Be(0);

        using var wrongRoadmap = await client.GetAsync($"/api/v1/roadmaps/{roadmap.Overview.Id + 1000}/generation-jobs/{jobId}");
        await ShouldFailAsync(wrongRoadmap, HttpStatusCode.NotFound, "JOB_NOT_FOUND");

        var other = await DiscoverySeed.RegisterAndLoginAsync(_factory, client, "TRAVELER");
        Authorize(client, other.AccessToken);
        using var foreignJob = await client.GetAsync($"/api/v1/roadmaps/{roadmap.Overview.Id}/generation-jobs/{jobId}");
        await ShouldFailAsync(foreignJob, HttpStatusCode.NotFound, "JOB_NOT_FOUND");
        using var foreignRoadmap = await client.PostAsJsonAsync(url, new GenerateRoadmapRequestDto { Scope = "full", Province = "Ho Chi Minh City" });
        await ShouldFailAsync(foreignRoadmap, HttpStatusCode.NotFound, "ROADMAP_NOT_FOUND");
    }

    private sealed record Seed(int PublicDishId, IReadOnlyList<int> ExtraDishIds);

    /// <summary>The public dish of <see cref="DiscoverySeed"/> plus three more dishes of the same restaurant.</summary>
    private async Task<Seed> SeedDishesAsync()
    {
        var publicDish = await DiscoverySeed.PublicDishAsync(_factory);
        var extraIds = await _factory.ExecuteDbAsync(async context =>
        {
            var extras = new[] { ("Bánh mì", 25000m), ("Cơm tấm", 50000m), ("Ốc len xào dừa", 60000m) }
                .Select(value => new Dish { RestaurantId = publicDish.RestaurantId, Name = value.Item1, Price = value.Item2 })
                .ToArray();
            context.Dishes.AddRange(extras);
            await context.SaveChangesAsync();
            return extras.Select(dish => dish.Id).ToArray();
        });
        return new Seed(publicDish.DishId, extraIds);
    }

    private static async Task<RoadmapDetailDto> CreatePinnedRoadmapAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/roadmaps", new CreateRoadmapRequestDto
        {
            Title = "AI trip",
            NumDays = 1,
            OriginMode = "pinned",
            OriginLatitude = 10.775m,
            OriginLongitude = 106.700m,
            OriginLabel = "Hotel"
        });
        response.EnsureSuccessStatusCode();
        return await SuccessDataAsync<RoadmapDetailDto>(response);
    }

    /// <summary>Polls RM-11 until the worker finishes the job.</summary>
    private static async Task<RoadmapGenerationJobDto> WaitForJobAsync(HttpClient client, long roadmapId, long jobId)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (true)
        {
            using var response = await client.GetAsync($"/api/v1/roadmaps/{roadmapId}/generation-jobs/{jobId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var job = await SuccessDataAsync<RoadmapGenerationJobDto>(response);
            if (job.Status is "succeeded" or "failed")
            {
                return job;
            }

            DateTime.UtcNow.Should().BeBefore(deadline, $"job {jobId} should finish, it is still {job.Status}");
            await Task.Delay(200);
        }
    }

    private static async Task<RoadmapDetailDto> GetDetailAsync(HttpClient client, long roadmapId)
    {
        using var response = await client.GetAsync($"/api/v1/roadmaps/{roadmapId}");
        response.EnsureSuccessStatusCode();
        return await SuccessDataAsync<RoadmapDetailDto>(response);
    }

    private static async Task ShouldFailAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be(errorCode);
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
