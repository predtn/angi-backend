using ANGI.Application.Common.Models.Discovery;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences.Repositories.Discovery;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;

namespace ANGI.IntegrationTests.Infrastructure.Persistences.Repositories.Discovery;

/// <summary>Runs the Agent's alternative tag groups on PostgreSQL.</summary>
public sealed class DishRepositoryTagGroupsTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    // TEST-01: "Cơm hoặc bún" keeps a dish with either tag, and every group must be met.
    /// <summary>Ensures a dish needs one tag of each group, not every tag.</summary>
    [PostgresEndToEndFact]
    public async Task GetCandidateIds_TagAnyGroups_NeedsOneTagOfEachGroup()
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            var rice = new Tag { TagType = TagType.Category, Code = "test.rice", Name = "Cơm" };
            var noodle = new Tag { TagType = TagType.Category, Code = "test.noodle", Name = "Bún" };
            var beef = new Tag { TagType = TagType.Ingredient, Code = "test.beef", Name = "Bò" };
            db.Tags.AddRange(rice, noodle, beef);
            await db.SaveChangesAsync();
            db.DishTags.Add(new DishTag { DishId = seed.DishId, TagId = rice.Id });
            await db.SaveChangesAsync();
            var repository = new DishRepository(db);

            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { TagAnyGroups = [[rice.Id, noodle.Id]] }, 10, default)).Should().Equal(seed.DishId);
            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { TagAnyGroups = [[noodle.Id]] }, 10, default)).Should().BeEmpty();
            (await repository.GetCandidateIdsAsync(new DishCandidateFilter { TagAnyGroups = [[rice.Id, noodle.Id], [beef.Id]] }, 10, default)).Should().BeEmpty();
        });
    }
}
