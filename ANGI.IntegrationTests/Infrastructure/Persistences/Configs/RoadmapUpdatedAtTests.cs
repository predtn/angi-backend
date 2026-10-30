using System.Text.Json;
using ANGI.Application.Common.Models.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.Infrastructure.Persistences.Configs;

/// <summary>Exercises the timestamp token against PostgreSQL, including raw SQL that bypasses EF auditing.</summary>
public sealed class RoadmapUpdatedAtTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    [PostgresEndToEndFact]
    public async Task ParentAndChildWrites_AdvanceTimestamp_AndRollbackRestoresIt()
    {
        var (roadmapId, _, _) = await SeedAsync();
        await factory.ExecuteDbAsync(async db =>
        {
            var roadmap = await db.Roadmaps.Include(value => value.Days).ThenInclude(day => day.Items).SingleAsync();
            var day = roadmap.Days.Single();
            var item = day.Items.Single();
            var timestamp = roadmap.UpdatedAt;
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                async Task AdvancedAsync()
                {
                    var next = await db.Roadmaps.AsNoTracking().Select(value => value.UpdatedAt).SingleAsync();
                    next.Should().BeAfter(timestamp);
                    (next.Ticks % 10).Should().Be(0);
                    timestamp = next;
                }
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE core.roadmaps SET title = 'Edited' WHERE id = {roadmapId}");
                await AdvancedAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE core.roadmap_days SET note = 'Edited day' WHERE id = {day.Id}");
                await AdvancedAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE core.roadmap_items SET sort_order = sort_order + 1 WHERE id = {item.Id}");
                await AdvancedAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO core.roadmap_days (roadmap_id, day_number) VALUES ({roadmapId}, 2)");
                await AdvancedAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO core.roadmap_items (roadmap_day_id, dish_id, meal_slot, sort_order, source) VALUES ({day.Id}, {item.DishId}, 'dinner', 1, 'manual')");
                await AdvancedAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM core.roadmap_items WHERE roadmap_day_id = {day.Id}");
                await AdvancedAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM core.roadmap_days WHERE roadmap_id = {roadmapId}");
                await AdvancedAsync();
                await transaction.RollbackAsync();
            }
            (await db.Roadmaps.AsNoTracking().Select(value => value.UpdatedAt).SingleAsync()).Should().Be(roadmap.UpdatedAt);
        });
    }

    [PostgresEndToEndFact]
    public async Task EfChildWrites_RefreshTrackedParent_WithFinalDatabaseTimestamp()
    {
        await SeedAsync();
        await factory.ExecuteDbAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var roadmap = await db.Roadmaps.Include(value => value.Days).ThenInclude(day => day.Items).SingleAsync();
            var previous = roadmap.UpdatedAt;
            roadmap.Days.Single().Items.Single().SortOrder++;
            roadmap.Days.Add(new RoadmapDay { DayNumber = 2 });
            await db.SaveChangesAsync();
            roadmap.UpdatedAt.Should().BeAfter(previous);
            roadmap.UpdatedAt.Should().Be(await db.Roadmaps.AsNoTracking().Select(value => value.UpdatedAt).SingleAsync());
            previous = roadmap.UpdatedAt;
            db.RoadmapItems.Remove(roadmap.Days.First().Items.Single());
            db.SaveChanges();
            roadmap.UpdatedAt.Should().BeAfter(previous);
            roadmap.UpdatedAt.Should().Be(await db.Roadmaps.AsNoTracking().Select(value => value.UpdatedAt).SingleAsync());
            await transaction.CommitAsync();
        });
    }

    [PostgresEndToEndFact]
    public async Task ClockBehindStoredTimestamp_AdvancesByMicrosecond_AndJsonPreservesExactToken()
    {
        var (roadmapId, _, _) = await SeedAsync();
        await factory.ExecuteDbAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var future = DateTime.UtcNow.Date.AddYears(1).AddTicks(1234560);
            // Seed a persisted future value, modelling a backwards wall-clock adjustment after an earlier write.
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE core.roadmaps DISABLE TRIGGER trg_roadmap_updated_at");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE core.roadmaps SET updated_at = {future} WHERE id = {roadmapId}");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE core.roadmaps ENABLE TRIGGER trg_roadmap_updated_at");
            foreach (var microsecond in new[] { 1, 2 })
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE core.roadmaps SET title = title WHERE id = {roadmapId}");
                var timestamp = await db.Roadmaps.AsNoTracking().Select(value => value.UpdatedAt).SingleAsync();
                timestamp.Should().Be(future.AddTicks(microsecond * 10));
                var snapshot = new AgentSnapshot(new() { SourceRoadmapUpdatedAt = timestamp }, [], [], roadmapId, timestamp);
                var stored = AgentJson.Read<AgentSnapshot>(AgentJson.Write(snapshot));
                stored.BaseRoadmapUpdatedAt.Should().Be(timestamp);
                stored.State.SourceRoadmapUpdatedAt.Should().Be(timestamp);
                var httpRoundTrip = JsonSerializer.Deserialize<AgentSnapshot>(JsonSerializer.Serialize(snapshot, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
                httpRoundTrip.BaseRoadmapUpdatedAt.Should().Be(timestamp);
            }
            await transaction.RollbackAsync();
        });
    }

    [PostgresEndToEndFact]
    public async Task MovingChildren_UpdatesBothParents()
    {
        var (roadmapId, userId, _) = await SeedAsync();
        await factory.ExecuteDbAsync(async db =>
        {
            var other = new ANGI.Domain.Entities.Roadmap { UserId = userId, Title = "Other", NumDays = 2,
                Days = [new RoadmapDay { DayNumber = 2 }] };
            db.Roadmaps.Add(other);
            await db.SaveChangesAsync();
            var originalTimestamp = await db.Roadmaps.Where(value => value.Id == roadmapId).Select(value => value.UpdatedAt).SingleAsync();
            var otherTimestamp = other.UpdatedAt;
            var item = await db.RoadmapItems.SingleAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE core.roadmap_items SET roadmap_day_id = {other.Days.Single().Id} WHERE id = {item.Id}");
            (await db.Roadmaps.AsNoTracking().Where(value => value.Id == roadmapId).Select(value => value.UpdatedAt).SingleAsync()).Should().BeAfter(originalTimestamp);
            (await db.Roadmaps.AsNoTracking().Where(value => value.Id == other.Id).Select(value => value.UpdatedAt).SingleAsync()).Should().BeAfter(otherTimestamp);
            originalTimestamp = await db.Roadmaps.AsNoTracking().Where(value => value.Id == roadmapId).Select(value => value.UpdatedAt).SingleAsync();
            otherTimestamp = await db.Roadmaps.AsNoTracking().Where(value => value.Id == other.Id).Select(value => value.UpdatedAt).SingleAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE core.roadmap_days SET roadmap_id = {other.Id} WHERE roadmap_id = {roadmapId}");
            (await db.Roadmaps.AsNoTracking().Where(value => value.Id == roadmapId).Select(value => value.UpdatedAt).SingleAsync()).Should().BeAfter(originalTimestamp);
            (await db.Roadmaps.AsNoTracking().Where(value => value.Id == other.Id).Select(value => value.UpdatedAt).SingleAsync()).Should().BeAfter(otherTimestamp);
        });
    }

    private async Task<(long RoadmapId, int UserId, int DishId)> SeedAsync()
    {
        await factory.ResetDatabaseAsync();
        var dish = await DiscoverySeed.PublicDishAsync(factory);
        using var client = factory.CreateClient();
        var auth = await DiscoverySeed.RegisterAndLoginAsync(factory, client, "TRAVELER");
        return await factory.ExecuteDbAsync(async db =>
        {
            var roadmap = new ANGI.Domain.Entities.Roadmap { UserId = auth.User.Id, Title = "Timestamp test", NumDays = 2,
                Days = [new RoadmapDay { DayNumber = 1, Items = [new RoadmapItem { DishId = dish.DishId,
                    MealSlot = MealSlot.Lunch, SortOrder = 1, Source = RoadmapItemSource.Manual }] }] };
            db.Roadmaps.Add(roadmap);
            await db.SaveChangesAsync();
            roadmap.UpdatedAt.Should().Be(await db.Roadmaps.AsNoTracking().Select(value => value.UpdatedAt).SingleAsync());
            return (roadmap.Id, auth.User.Id, dish.DishId);
        });
    }
}
