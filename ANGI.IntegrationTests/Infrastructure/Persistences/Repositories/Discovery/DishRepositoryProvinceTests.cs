using ANGI.Application.Common.Models.Discovery;
using ANGI.Infrastructure.Persistences.Repositories.Discovery;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.Infrastructure.Persistences.Repositories.Discovery;

/// <summary>Runs the dish province filters on PostgreSQL, where the catalog's full names meet the names restaurants store.</summary>
public sealed class DishRepositoryProvinceTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    // TEST-01: A restaurant stored with a short name is a candidate for its short name and the catalog's full name, not another province.
    /// <summary>Ensures "Thành phố Hà Nội" finds dishes of a restaurant stored as "Hà Nội", the case that left RM-10 with no candidates.</summary>
    [PostgresEndToEndFact]
    public async Task GetCandidateIds_ShortStoredName_MatchesFullCatalogName()
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            var restaurant = await db.Restaurants.SingleAsync(x => x.Id == seed.RestaurantId);
            restaurant.ProvinceName = "Hà Nội";
            await db.SaveChangesAsync();
            var repository = new DishRepository(db);

            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { Province = "Thành phố Hà Nội" }, 10, default))
                .Should().Equal(seed.DishId);
            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { Province = "Hà Nội" }, 10, default))
                .Should().Equal(seed.DishId);
            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { Province = "Thành phố Đà Nẵng" }, 10, default))
                .Should().BeEmpty();
        });
    }

    // TEST-02: A restaurant stored with the catalog's full name is still found by both spellings.
    /// <summary>Ensures dropping the prefix from the search keeps restaurants registered through the province catalog.</summary>
    [PostgresEndToEndFact]
    public async Task GetCandidateIds_FullStoredName_MatchesBothSpellings()
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            var restaurant = await db.Restaurants.SingleAsync(x => x.Id == seed.RestaurantId);
            restaurant.ProvinceName = "Thành phố Hà Nội";
            await db.SaveChangesAsync();
            var repository = new DishRepository(db);

            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { Province = "Thành phố Hà Nội" }, 10, default))
                .Should().Equal(seed.DishId);
            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { Province = "Hà Nội" }, 10, default))
                .Should().Equal(seed.DishId);
        });
    }

    // TEST-03: The trending list of HOME-01 applies the same province rule.
    /// <summary>Ensures the home screen's trending dishes do not come back empty for a catalog province name.</summary>
    [PostgresEndToEndFact]
    public async Task GetTrendingIds_ShortStoredName_MatchesFullCatalogName()
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            var restaurant = await db.Restaurants.SingleAsync(x => x.Id == seed.RestaurantId);
            restaurant.ProvinceName = "Hà Nội";
            await db.SaveChangesAsync();
            var repository = new DishRepository(db);

            (await repository.GetTrendingIdsAsync("Thành phố Hà Nội", DateTime.UtcNow.AddDays(-7), 10, default))
                .Should().Equal(seed.DishId);
            (await repository.GetTrendingIdsAsync("Thành phố Đà Nẵng", DateTime.UtcNow.AddDays(-7), 10, default))
                .Should().BeEmpty();
        });
    }
}
