using ANGI.Application.DTOs.Restaurant;
using ANGI.Infrastructure.Persistences.Repositories.Restaurant;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.Infrastructure.Persistences.Repositories.Restaurant;

/// <summary>Runs the restaurant list's province filter on PostgreSQL with the province catalog's full names.</summary>
public sealed class RestaurantRepositoryProvinceTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    // TEST-01: A restaurant stored with a short name is listed for the catalog's full name and not for another province.
    /// <summary>Ensures "Thành phố Hà Nội" finds a restaurant stored as "Hà Nội" in the public list.</summary>
    [PostgresEndToEndFact]
    public async Task Browse_ShortStoredName_MatchesFullCatalogName()
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            var restaurant = await db.Restaurants.SingleAsync(x => x.Id == seed.RestaurantId);
            restaurant.ProvinceName = "Hà Nội";
            await db.SaveChangesAsync();
            var repository = new RestaurantRepository(db);

            var found = await repository.BrowseAsync(Request("Thành phố Hà Nội"), DateTimeOffset.UtcNow, default);
            found.Items.Select(item => item.Id).Should().Equal(seed.RestaurantId);
            var other = await repository.BrowseAsync(Request("Thành phố Đà Nẵng"), DateTimeOffset.UtcNow, default);
            other.Items.Should().BeEmpty();
        });
    }

    /// <summary>The first page of the public list, sorted by rating, for one province.</summary>
    private static BrowseRestaurantsRequestDto Request(string province) =>
        new() { Province = province, Sort = "rating", Page = 1, PageSize = 10 };
}
