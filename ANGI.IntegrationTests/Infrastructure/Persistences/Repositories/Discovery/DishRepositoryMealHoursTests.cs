using ANGI.Application.Common.Models.Discovery;
using ANGI.Domain.Entities;
using ANGI.Infrastructure.Persistences.Repositories.Discovery;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.Infrastructure.Persistences.Repositories.Discovery;

/// <summary>Runs the business-hours candidate filter on PostgreSQL, where the shared MealHours rule is translated to SQL.</summary>
public sealed class DishRepositoryMealHoursTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    // TEST-01: A restaurant without business hours stays a candidate for every meal.
    /// <summary>Ensures missing hours never remove a dish.</summary>
    [PostgresEndToEndFact]
    public async Task GetCandidateIds_NoBusinessHours_KeepsDish()
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            var repository = new DishRepository(db);

            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { OpenForMeal = "dinner", OpenOnDays = [0] }, 10, default))
                .Should().Equal(seed.DishId);
        });
    }

    // TEST-02: Declared hours must reach the meal, on every requested weekday or on some day when none is given.
    /// <summary>Ensures a Monday-to-Saturday morning restaurant serves breakfast except on Sunday and never dinner.</summary>
    [PostgresEndToEndFact]
    public async Task GetCandidateIds_MorningOnlyClosedSunday_FiltersByMealAndWeekday()
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            var restaurant = await db.Restaurants.Include(x => x.BusinessHours).SingleAsync(x => x.Id == seed.RestaurantId);
            for (short day = 1; day <= 6; day++)
                restaurant.BusinessHours.Add(new RestaurantBusinessHour { DayOfWeek = day, OpenTime = new(6, 0), CloseTime = new(11, 0) });
            await db.SaveChangesAsync();
            var repository = new DishRepository(db);

            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { OpenForMeal = "breakfast" }, 10, default)).Should().Equal(seed.DishId);
            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { OpenForMeal = "breakfast", OpenOnDays = [1, 2] }, 10, default)).Should().Equal(seed.DishId);
            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { OpenForMeal = "breakfast", OpenOnDays = [1, 0] }, 10, default)).Should().BeEmpty();
            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { OpenForMeal = "dinner" }, 10, default)).Should().BeEmpty();
        });
    }

    // TEST-02b: "Right now" keeps a restaurant only while it stays open for 30 more minutes.
    /// <summary>Ensures a restaurant closing at 10:30 is a candidate at 10:00 but not at 10:29.</summary>
    [PostgresEndToEndFact]
    public async Task GetCandidateIds_OpenAtTime_NeedsThirtyMinutesLeft()
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            var restaurant = await db.Restaurants.Include(x => x.BusinessHours).SingleAsync(x => x.Id == seed.RestaurantId);
            restaurant.BusinessHours.Add(new RestaurantBusinessHour { DayOfWeek = 1, OpenTime = new(9, 0), CloseTime = new(10, 30) });
            await db.SaveChangesAsync();
            var repository = new DishRepository(db);

            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { OpenAtTime = new(10, 0), OpenOnDays = [1] }, 10, default)).Should().Equal(seed.DishId);
            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { OpenAtTime = new(10, 29), OpenOnDays = [1] }, 10, default)).Should().BeEmpty();
        });
    }

    // TEST-03: A row past midnight reaches late night on its own day and the next day's early hours.
    /// <summary>Ensures overnight hours are translated across the day boundary.</summary>
    [PostgresEndToEndFact]
    public async Task GetCandidateIds_OvernightHours_ReachesLateNight()
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            var restaurant = await db.Restaurants.Include(x => x.BusinessHours).SingleAsync(x => x.Id == seed.RestaurantId);
            restaurant.BusinessHours.Add(new RestaurantBusinessHour { DayOfWeek = 4, OpenTime = new(18, 0), CloseTime = new(7, 0) });
            await db.SaveChangesAsync();
            var repository = new DishRepository(db);

            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { OpenForMeal = "late_night", OpenOnDays = [4] }, 10, default)).Should().Equal(seed.DishId);
            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { OpenForMeal = "breakfast", OpenOnDays = [5] }, 10, default)).Should().Equal(seed.DishId);
            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { OpenForMeal = "lunch", OpenOnDays = [5] }, 10, default)).Should().BeEmpty();
        });
    }
}
