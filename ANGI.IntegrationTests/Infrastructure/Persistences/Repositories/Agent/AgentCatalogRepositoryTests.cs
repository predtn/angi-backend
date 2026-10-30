using ANGI.Application.Common.Models.Discovery;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences.Repositories.Agent;
using ANGI.Infrastructure.Persistences.Repositories.Discovery;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.Infrastructure.Persistences.Repositories.Agent;

/// <summary>Runs actual PostgreSQL catalog lookup and Agent-only filtering against a dedicated database.</summary>
public sealed class AgentCatalogRepositoryTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    [PostgresEndToEndFact]
    public async Task Lookup_EscapesWildcardsAndSeparatesUnavailableFromMissing()
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            var dish = await db.Dishes.SingleAsync(x => x.Id == seed.DishId);
            dish.Name = "Món 100%"; dish.IsAvailable = false;
            db.Dishes.Add(new Dish { RestaurantId = seed.RestaurantId, Name = "Món 1000", Price = 30000 });
            await db.SaveChangesAsync();
            var repository = new AgentCatalogRepository(db);
            var matches = await repository.SearchAsync("dish", "100%", null, 21, default);
            matches.Should().ContainSingle(); matches[0].Id.Should().Be(seed.DishId); matches[0].Available.Should().BeFalse();
            (await repository.SearchAsync("dish", "Món đã ẩn", null, 21, default)).Should().BeEmpty();
            (await repository.SearchAsync("dish", "Món không tồn tại", null, 21, default)).Should().BeEmpty();
            var restaurant = await db.Restaurants.SingleAsync(x => x.Id == seed.RestaurantId);
            restaurant.OperatingStatus = RestaurantOperatingStatus.TemporarilyClosed;
            await db.SaveChangesAsync();
            var restaurants = await repository.SearchAsync("restaurant", restaurant.Name.ToLowerInvariant(), "ho chi minh city", 21, default);
            restaurants.Should().ContainSingle(); restaurants[0].Available.Should().BeFalse();
        });
    }

    [PostgresEndToEndFact]
    public async Task CandidateLimit_ComesAfterAllAgentTagsAndMealCompatibility()
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            var first = new Tag { Code = "agent_one", Name = "One", TagType = TagType.Category };
            var second = new Tag { Code = "agent_two", Name = "Two", TagType = TagType.Category };
            var breakfast = new Tag { Code = "breakfast", Name = "Breakfast", TagType = TagType.MealTime };
            var wrong = new Dish { RestaurantId = seed.RestaurantId, Name = "Not all tags", Price = 10000, LikeCount = 100 };
            wrong.DishTags.Add(new() { Tag = first });
            var wrongMeal = new Dish { RestaurantId = seed.RestaurantId, Name = "Wrong meal", Price = 10000, LikeCount = 90 };
            wrongMeal.DishTags.Add(new() { Tag = first }); wrongMeal.DishTags.Add(new() { Tag = second }); wrongMeal.DishTags.Add(new() { Tag = breakfast });
            var correct = new Dish { RestaurantId = seed.RestaurantId, Name = "All tags, untagged meal", Price = 10000 };
            correct.DishTags.Add(new() { Tag = first }); correct.DishTags.Add(new() { Tag = second });
            db.Dishes.AddRange(wrong, wrongMeal, correct); await db.SaveChangesAsync();
            var repository = new DishRepository(db);
            var candidates = await repository.GetCandidateIdsAsync(new() { TagIds = [first.Id, second.Id], RequireAllTags = true,
                CompatibleMealTimeCode = "lunch" }, 1, default);
            candidates.Should().Equal(correct.Id);
            var named = await repository.GetCandidateIdsAsync(new DishCandidateFilter { DishIds = [correct.Id], RestaurantId = seed.RestaurantId }, 10, default);
            named.Should().Equal(correct.Id);
        });
    }
}
