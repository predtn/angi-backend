using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.AddMenuSubmissionItem;
using ANGI.Application.UseCases.Restaurant.CancelMenuSubmission;
using ANGI.Application.UseCases.Restaurant.DeleteMenuSubmissionItem;
using ANGI.Application.UseCases.Restaurant.SubmitMenuSubmission;
using ANGI.Application.UseCases.Restaurant.UpdateMenuSubmissionItem;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences;
using ANGI.Infrastructure.Persistences.Repositories;
using ANGI.Infrastructure.Persistences.Repositories.Restaurant;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;

namespace ANGI.Test.Infrastructure.Persistences.Repositories.Restaurant;

public sealed class MenuSubmissionPostgresConcurrencyTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc);

    // TEST-01: Serialize two simultaneous submit requests so exactly one transition succeeds.
    [PostgresIntegrationFact]
    public async Task Submit_WithTwoDatabaseScopes_ShouldAllowExactlyOneTransition()
    {
        var connectionString = RequiredConnectionString();
        var seed = await SeedDraftAsync(connectionString);

        try
        {
            var gate = NewGate();
            var first = RunSubmitAsync(connectionString, seed, gate.Task);
            var second = RunSubmitAsync(connectionString, seed, gate.Task);
            gate.SetResult();

            var outcomes = await Task.WhenAll(first, second);

            outcomes.Should().ContainSingle(outcome => outcome == "success");
            outcomes.Should().ContainSingle(outcome => outcome == "SUBMISSION_NOT_EDITABLE");
            (await LoadStatusAsync(connectionString, seed.SubmissionId))
                .Should().Be(MenuSubmissionStatus.Pending);
        }
        finally
        {
            await DeleteSeedAsync(connectionString, seed);
        }
    }

    // TEST-02: Serialize simultaneous submit and cancel without leaving a pending cancelled workflow.
    [PostgresIntegrationFact]
    public async Task Submit_ConcurrentWithCancel_ShouldFinishCancelled()
    {
        var connectionString = RequiredConnectionString();
        var seed = await SeedDraftAsync(connectionString);

        try
        {
            var gate = NewGate();
            var submit = RunSubmitAsync(connectionString, seed, gate.Task);
            var cancel = RunCancelAsync(connectionString, seed, gate.Task);
            gate.SetResult();

            var outcomes = await Task.WhenAll(submit, cancel);

            outcomes[1].Should().Be("success");
            outcomes[0].Should().BeOneOf("success", "SUBMISSION_NOT_EDITABLE");
            (await LoadStatusAsync(connectionString, seed.SubmissionId))
                .Should().Be(MenuSubmissionStatus.Cancelled);
        }
        finally
        {
            await DeleteSeedAsync(connectionString, seed);
        }
    }

    // TEST-03: Serialize submit against an add-item command using PostgreSQL locks.
    [PostgresIntegrationFact]
    public Task Submit_ConcurrentWithAddItem_ShouldNeverAddAfterPending() =>
        AssertConcurrentMutationAsync("add");

    // TEST-04: Serialize submit against an update-item command using PostgreSQL locks.
    [PostgresIntegrationFact]
    public Task Submit_ConcurrentWithUpdateItem_ShouldNeverUpdateAfterPending() =>
        AssertConcurrentMutationAsync("update");

    // TEST-05: Serialize submit against a delete-item command using PostgreSQL locks.
    [PostgresIntegrationFact]
    public Task Submit_ConcurrentWithDeleteItem_ShouldNeverDeleteAfterPending() =>
        AssertConcurrentMutationAsync("delete");

    // TEST-06: Hide a submission whose owning restaurant has been soft deleted.
    [PostgresIntegrationFact]
    public async Task Submit_WithSoftDeletedRestaurant_ShouldReturnNotFound()
    {
        var connectionString = RequiredConnectionString();
        var seed = await SeedDraftAsync(connectionString);

        try
        {
            await using (var context = CreateContext(connectionString))
            {
                await context.Restaurants.IgnoreQueryFilters()
                    .Where(restaurant => restaurant.Id == seed.RestaurantId)
                    .ExecuteUpdateAsync(update => update.SetProperty(
                        restaurant => restaurant.DeletedAt,
                        Now));
            }

            var outcome = await RunSubmitAsync(connectionString, seed, Task.CompletedTask);

            outcome.Should().Be("SUBMISSION_NOT_FOUND");
            (await LoadStatusAsync(connectionString, seed.SubmissionId))
                .Should().Be(MenuSubmissionStatus.Draft);
        }
        finally
        {
            await DeleteSeedAsync(connectionString, seed);
        }
    }

    private static async Task AssertConcurrentMutationAsync(string operation)
    {
        var connectionString = RequiredConnectionString();
        var seed = await SeedDraftAsync(connectionString);

        try
        {
            var gate = NewGate();
            var submit = RunSubmitAsync(connectionString, seed, gate.Task);
            var mutation = RunMutationAsync(connectionString, seed, operation, gate.Task);
            gate.SetResult();

            var outcomes = await Task.WhenAll(submit, mutation);

            outcomes[0].Should().Be("success", operation);
            outcomes[1].Should().BeOneOf("success", "SUBMISSION_NOT_EDITABLE");
            (await LoadStatusAsync(connectionString, seed.SubmissionId))
                .Should().Be(MenuSubmissionStatus.Pending);
        }
        finally
        {
            await DeleteSeedAsync(connectionString, seed);
        }
    }

    private static async Task<string> RunSubmitAsync(
        string connectionString,
        SeedResult seed,
        Task gate)
    {
        await using var context = CreateContext(connectionString);
        var repository = new RestaurantMenuRepository(context);
        var useCase = new SubmitMenuSubmissionUseCase(
            new SubmitMenuSubmissionRequestDtoValidator(),
            repository,
            CurrentUser(seed.OwnerId),
            new UnitOfWork(context),
            new FixedTimeProvider(Now));

        await gate;
        return await OutcomeAsync(() => useCase.ExecuteAsync(
            seed.SubmissionId,
            new SubmitMenuSubmissionRequestDto { OwnerNote = "Gửi duyệt" },
            CancellationToken.None));
    }

    private static async Task<string> RunCancelAsync(
        string connectionString,
        SeedResult seed,
        Task gate)
    {
        await using var context = CreateContext(connectionString);
        var repository = new RestaurantMenuRepository(context);
        var useCase = new CancelMenuSubmissionUseCase(
            repository,
            CurrentUser(seed.OwnerId),
            new UnitOfWork(context));

        await gate;
        return await OutcomeAsync(() => useCase.ExecuteAsync(seed.SubmissionId, CancellationToken.None));
    }

    private static Task<string> RunMutationAsync(
        string connectionString,
        SeedResult seed,
        string operation,
        Task gate) => operation switch
        {
            "add" => RunAddAsync(connectionString, seed, gate),
            "update" => RunUpdateAsync(connectionString, seed, gate),
            "delete" => RunDeleteAsync(connectionString, seed, gate),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

    private static async Task<string> RunAddAsync(
        string connectionString,
        SeedResult seed,
        Task gate)
    {
        await using var context = CreateContext(connectionString);
        var useCase = new AddMenuSubmissionItemUseCase(
            new AddMenuSubmissionItemRequestDtoValidator(),
            new RestaurantMenuRepository(context),
            Mock.Of<IMediaRepository>(),
            CurrentUser(seed.OwnerId),
            new UnitOfWork(context));

        await gate;
        return await OutcomeAsync(() => useCase.ExecuteAsync(
            seed.SubmissionId,
            new AddMenuSubmissionItemRequestDto
            {
                Name = "Món thêm đồng thời",
                Description = "Mô tả hợp lệ dài hơn ba mươi ký tự.",
                DishForm = "dry",
                Price = 45000,
                TagIds = []
            },
            CancellationToken.None));
    }

    private static async Task<string> RunUpdateAsync(
        string connectionString,
        SeedResult seed,
        Task gate)
    {
        await using var context = CreateContext(connectionString);
        var useCase = new UpdateMenuSubmissionItemUseCase(
            new UpdateMenuSubmissionItemRequestDtoValidator(),
            new RestaurantMenuRepository(context),
            Mock.Of<IMediaRepository>(),
            CurrentUser(seed.OwnerId),
            new UnitOfWork(context));

        await gate;
        return await OutcomeAsync(() => useCase.ExecuteAsync(
            seed.SubmissionId,
            seed.SourceItemId,
            new UpdateMenuSubmissionItemRequestDto { Name = "Tên món cập nhật đồng thời" },
            CancellationToken.None));
    }

    private static async Task<string> RunDeleteAsync(
        string connectionString,
        SeedResult seed,
        Task gate)
    {
        await using var context = CreateContext(connectionString);
        var useCase = new DeleteMenuSubmissionItemUseCase(
            new RestaurantMenuRepository(context),
            CurrentUser(seed.OwnerId),
            new UnitOfWork(context));

        await gate;
        return await OutcomeAsync(() => useCase.ExecuteAsync(
            seed.SubmissionId,
            seed.NewItemId,
            CancellationToken.None));
    }

    private static async Task<string> OutcomeAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return "success";
        }
        catch (AppException exception)
        {
            return exception.ErrorCode;
        }
    }

    private static async Task<SeedResult> SeedDraftAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var owner = new User
        {
            Email = $"angi-76-{suffix}@integration.test",
            RoleId = 2,
            DisplayName = "ANGI 76 Integration",
            Status = UserStatus.Active,
            EmailVerifiedAt = Now
        };
        context.Users.Add(owner);
        await context.SaveChangesAsync();

        var restaurant = new ANGI.Domain.Entities.Restaurant
        {
            OwnerId = owner.Id,
            Name = $"ANGI 76 {suffix}",
            Slug = $"angi-76-{suffix}",
            AddressLine = "Test address",
            Latitude = 10.775m,
            Longitude = 106.700m
        };
        context.Restaurants.Add(restaurant);
        await context.SaveChangesAsync();

        var dish = new Dish
        {
            RestaurantId = restaurant.Id,
            Name = "Món hiện tại",
            Description = "Mô tả món hiện tại đủ dài để kiểm thử.",
            DishForm = DishForm.Soup,
            Price = 50000,
            SortOrder = 1
        };
        context.Dishes.Add(dish);
        await context.SaveChangesAsync();

        var submission = new MenuSubmission
        {
            RestaurantId = restaurant.Id,
            CreatedBy = owner.Id,
            Status = MenuSubmissionStatus.Draft,
            Items =
            [
                new MenuSubmissionItem
                {
                    SourceDishId = dish.Id,
                    Name = dish.Name,
                    Description = dish.Description,
                    DishForm = dish.DishForm,
                    Price = dish.Price + 1,
                    SortOrder = dish.SortOrder,
                    TagIds = []
                },
                new MenuSubmissionItem
                {
                    Name = "Món mới trong draft",
                    Description = "Mô tả món mới đủ dài để kiểm thử.",
                    DishForm = DishForm.Dry,
                    Price = 30000,
                    SortOrder = 2,
                    TagIds = []
                }
            ]
        };
        context.MenuSubmissions.Add(submission);
        await context.SaveChangesAsync();

        return new SeedResult(
            owner.Id,
            restaurant.Id,
            submission.Id,
            submission.Items.Single(item => item.SourceDishId.HasValue).Id,
            submission.Items.Single(item => !item.SourceDishId.HasValue).Id);
    }

    private static async Task<MenuSubmissionStatus> LoadStatusAsync(
        string connectionString,
        long submissionId)
    {
        await using var context = CreateContext(connectionString);
        return await context.MenuSubmissions.IgnoreQueryFilters()
            .Where(submission => submission.Id == submissionId)
            .Select(submission => submission.Status)
            .SingleAsync();
    }

    private static async Task DeleteSeedAsync(string connectionString, SeedResult seed)
    {
        await using var context = CreateContext(connectionString);
        await context.MenuSubmissionItems
            .Where(item => item.SubmissionId == seed.SubmissionId)
            .ExecuteDeleteAsync();
        await context.MenuSubmissions.IgnoreQueryFilters()
            .Where(submission => submission.Id == seed.SubmissionId)
            .ExecuteDeleteAsync();
        await context.Dishes.Where(dish => dish.RestaurantId == seed.RestaurantId).ExecuteDeleteAsync();
        await context.Restaurants.IgnoreQueryFilters()
            .Where(restaurant => restaurant.Id == seed.RestaurantId)
            .ExecuteDeleteAsync();
        await context.Users.IgnoreQueryFilters()
            .Where(user => user.Id == seed.OwnerId)
            .ExecuteDeleteAsync();
    }

    private static ICurrentUserService CurrentUser(int ownerId)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns(ownerId);
        return currentUser.Object;
    }

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ANGIContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ANGIContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ANGIContext(options, new FixedTimeProvider(Now));
    }

    private static string RequiredConnectionString() =>
        Environment.GetEnvironmentVariable(PostgresIntegrationFactAttribute.ConnectionStringVariable)!;

    private sealed record SeedResult(
        int OwnerId,
        long RestaurantId,
        long SubmissionId,
        long SourceItemId,
        long NewItemId);

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        private readonly DateTimeOffset _now = new(now);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}

public sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    public const string ConnectionStringVariable = "ANGI_TEST_POSTGRES_CONNECTION_STRING";

    public PostgresIntegrationFactAttribute()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (Environment.GetEnvironmentVariable("RUN_POSTGRES_INTEGRATION_TESTS") != "1" ||
            string.IsNullOrWhiteSpace(connectionString))
        {
            Skip = $"Set RUN_POSTGRES_INTEGRATION_TESTS=1 and {ConnectionStringVariable} to a dedicated test database.";
            return;
        }

        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrWhiteSpace(database) ||
            !database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "The PostgreSQL integration database name must contain 'test' to protect development data.";
        }
    }
}
