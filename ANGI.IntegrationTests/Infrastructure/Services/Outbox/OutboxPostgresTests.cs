using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences;
using ANGI.Infrastructure.Services.Outbox;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ANGI.IntegrationTests.Infrastructure.Services.Outbox;

/// <summary>
/// The outbox worker on PostgreSQL: rows written by the InitialCreate triggers, claimed with SKIP LOCKED in
/// aggregate_key order, and settled by the dispatcher (project_architecture.md, Outbox).
/// </summary>
public sealed class OutboxPostgresTests : IAsyncLifetime
{
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable(PostgresIntegrationFactAttribute.ConnectionStringVariable) ?? string.Empty;

    public async Task InitializeAsync()
    {
        if (_connectionString.Length == 0)
        {
            return;
        }

        await using var context = Context();
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM core.outbox_messages");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // TEST-01: The triggers write the RECO payloads: insert and rename → DISH_UPSERTED, hide → DISH_DEACTIVATED.
    [PostgresIntegrationFact]
    public async Task Triggers_ShouldWriteDishEventsInOrder()
    {
        var dishId = await InsertDishAsync();
        await using (var context = Context())
        {
            var dish = await context.Dishes.SingleAsync(value => value.Id == dishId);
            dish.Name = "Bún bò Huế đặc biệt";
            await context.SaveChangesAsync();
            dish.Price = 99000;
            dish.IsAvailable = false;
            await context.SaveChangesAsync();
            dish.Status = DishStatus.Hidden;
            await context.SaveChangesAsync();
        }

        var rows = await RowsAsync();
        rows.Select(row => row.EventType).Should().Equal(
            OutboxEventType.DishUpserted, OutboxEventType.DishUpserted, OutboxEventType.DishDeactivated);
        rows.Should().OnlyContain(row => row.AggregateKey == $"dish:{dishId}" && row.Status == OutboxStatus.Pending);
        rows[1].Payload.Should().Contain("\"dish_id\": " + dishId).And.Contain("Bún bò Huế đặc biệt").And.Contain("\"occurred_at\"");
    }

    // TEST-02: Only the oldest unfinished event of a key is claimed; other keys go in the same batch.
    [PostgresIntegrationFact]
    public async Task Claim_ShouldTakeOnlyTheHeadOfEachKey()
    {
        var first = await InsertDishAsync();
        var second = await InsertDishAsync();
        await HideAsync(first);

        await using var context = Context();
        var claimed = await new OutboxStore(context).ClaimAsync(50, TimeSpan.FromMinutes(1), CancellationToken.None);

        claimed.Select(row => (row.AggregateKey, row.EventType)).Should().Equal(
            ($"dish:{first}", OutboxEventType.DishUpserted),
            ($"dish:{second}", OutboxEventType.DishUpserted));
        (await RowsAsync()).Count(row => row.Status == OutboxStatus.Processing).Should().Be(2);

        // A second worker finds nothing: the heads are locked, the follower waits for its head.
        await using var other = Context();
        (await new OutboxStore(other).ClaimAsync(50, TimeSpan.FromMinutes(1), CancellationToken.None)).Should().BeEmpty();
    }

    // TEST-03: Delivered → done; a retry blocks the key's next event until it is due; 4xx → dead and the key moves on.
    [PostgresIntegrationFact]
    public async Task Dispatch_ShouldSettleRowsAndKeepKeyOrder()
    {
        var retried = await InsertDishAsync();
        await HideAsync(retried);
        var dead = await InsertDishAsync();
        await HideAsync(dead);
        var delivered = await InsertDishAsync();
        var sender = new ScriptedSender(message =>
            message.AggregateKey == $"dish:{retried}" ? new OutboxDeliveryResult(OutboxDeliveryOutcome.Retry, "HTTP 503")
            : message.AggregateKey == $"dish:{dead}" && message.EventType == OutboxEventType.DishUpserted
                ? new OutboxDeliveryResult(OutboxDeliveryOutcome.Dead, "HTTP 422 VALIDATION_FAILED")
                : OutboxDeliveryResult.Delivered);

        (await DispatchAsync(sender)).Should().Be(3);
        (await DispatchAsync(sender)).Should().Be(1, "after the dead upsert, that dish's deactivation is next");
        (await DispatchAsync(sender)).Should().Be(0, "the retried head is not due yet and blocks its follower");

        var rows = await RowsAsync();
        Row(rows, retried, OutboxEventType.DishUpserted).Should().Match<OutboxMessage>(row =>
            row.Status == OutboxStatus.Pending && row.AttemptCount == 1 && row.LastError == "HTTP 503" &&
            row.LockedUntil == null);
        (Row(rows, retried, OutboxEventType.DishUpserted).NextAttemptAt - DateTime.UtcNow)
            .Should().BeCloseTo(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));
        Row(rows, retried, OutboxEventType.DishDeactivated).Status.Should().Be(OutboxStatus.Pending);
        Row(rows, dead, OutboxEventType.DishUpserted).Should().Match<OutboxMessage>(row =>
            row.Status == OutboxStatus.Dead && row.LastError == "HTTP 422 VALIDATION_FAILED");
        Row(rows, dead, OutboxEventType.DishDeactivated).Status.Should().Be(OutboxStatus.Done);
        Row(rows, delivered, OutboxEventType.DishUpserted).Should().Match<OutboxMessage>(row =>
            row.Status == OutboxStatus.Done && row.ProcessedAt != null && row.AttemptCount == 1);
    }

    // TEST-04: The last allowed attempt turns a retry into dead; Retry-After is used as the delay.
    [PostgresIntegrationFact]
    public async Task Dispatch_ShouldStopRetryingAtMaxAttemptsAndHonourRetryAfter()
    {
        var exhausted = await InsertDishAsync();
        var throttled = await InsertDishAsync();
        await using (var context = Context())
        {
            await context.OutboxMessages.Where(row => row.AggregateKey == $"dish:{exhausted}")
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.AttemptCount, (short)9));
        }

        var sender = new ScriptedSender(message => message.AggregateKey == $"dish:{throttled}"
            ? new OutboxDeliveryResult(OutboxDeliveryOutcome.Retry, "HTTP 503", TimeSpan.FromSeconds(30))
            : new OutboxDeliveryResult(OutboxDeliveryOutcome.Retry, "Timeout"));
        await DispatchAsync(sender);

        var rows = await RowsAsync();
        Row(rows, exhausted, OutboxEventType.DishUpserted).Should().Match<OutboxMessage>(row =>
            row.Status == OutboxStatus.Dead && row.AttemptCount == 10);
        (Row(rows, throttled, OutboxEventType.DishUpserted).NextAttemptAt - DateTime.UtcNow)
            .Should().BeCloseTo(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3));
    }

    // TEST-05: A row left processing by a crashed worker is claimed again once its lock expires.
    [PostgresIntegrationFact]
    public async Task Claim_ShouldReclaimExpiredLocks()
    {
        var dishId = await InsertDishAsync();
        await using (var context = Context())
        {
            await new OutboxStore(context).ClaimAsync(50, TimeSpan.FromSeconds(1), CancellationToken.None);
            await context.OutboxMessages.Where(row => row.AggregateKey == $"dish:{dishId}")
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LockedUntil, DateTime.UtcNow.AddMinutes(-1)));
        }

        await using var again = Context();
        (await new OutboxStore(again).ClaimAsync(50, TimeSpan.FromMinutes(1), CancellationToken.None))
            .Should().ContainSingle(row => row.AggregateKey == $"dish:{dishId}");
    }

    private ANGIContext Context() => PostgresTestDatabase.CreateContext(_connectionString, TimeProvider.System);

    private async Task<int> DispatchAsync(IOutboxSender sender)
    {
        await using var context = Context();
        var dispatcher = new OutboxDispatcher(
            new OutboxStore(context),
            sender,
            Options.Create(new OutboxSettings { BatchSize = 50, MaxAttempts = 10, TimeoutSeconds = 1 }),
            NullLogger<OutboxDispatcher>.Instance);
        return await dispatcher.DispatchOnceAsync(CancellationToken.None);
    }

    /// <summary>Inserts an active dish (firing DISH_UPSERTED) in a restaurant of a new owner.</summary>
    private async Task<int> InsertDishAsync()
    {
        await using var context = Context();
        var owner = new User
        {
            Email = $"outbox-{Guid.NewGuid():N}@integration.test",
            RoleId = 2,
            DisplayName = "Outbox Owner",
            Status = UserStatus.Active
        };
        var restaurant = new Restaurant
        {
            Owner = owner,
            Name = "Outbox Restaurant",
            Slug = $"outbox-{Guid.NewGuid():N}",
            Phone = "0900000000",
            AddressLine = "1 Outbox Street",
            District = "District 1",
            ProvinceName = "Ho Chi Minh City",
            Latitude = 10.775m,
            Longitude = 106.700m
        };
        var dish = new Dish { Restaurant = restaurant, Name = "Bún bò", Price = 45000 };
        context.Dishes.Add(dish);
        await context.SaveChangesAsync();
        return dish.Id;
    }

    private async Task HideAsync(int dishId)
    {
        await using var context = Context();
        await context.Dishes.Where(dish => dish.Id == dishId)
            .ExecuteUpdateAsync(set => set.SetProperty(dish => dish.Status, DishStatus.Hidden));
    }

    private async Task<List<OutboxMessage>> RowsAsync()
    {
        await using var context = Context();
        return await context.OutboxMessages.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
    }

    private static OutboxMessage Row(IEnumerable<OutboxMessage> rows, int dishId, OutboxEventType type) =>
        rows.Single(row => row.AggregateKey == $"dish:{dishId}" && row.EventType == type);

    private sealed class ScriptedSender(Func<OutboxMessage, OutboxDeliveryResult> script) : IOutboxSender
    {
        public bool IsConfigured => true;

        public Task<OutboxDeliveryResult> SendAsync(OutboxMessage message, CancellationToken ct) =>
            Task.FromResult(script(message));
    }
}
