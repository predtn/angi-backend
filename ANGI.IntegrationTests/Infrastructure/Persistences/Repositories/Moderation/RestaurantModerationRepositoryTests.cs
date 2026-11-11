using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Moderation.ExpireUserSuspensions;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.IntegrationTests.Infrastructure.Persistences.Repositories.Moderation;

/// <summary>Checks real PostgreSQL expiry boundaries and locking through the moderation repository.</summary>
public sealed class RestaurantModerationRepositoryTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private static readonly DateTime Now = new(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);

    // TEST-01: Expire deadlines before or equal to now, preserving future, indefinite, banned and deleted accounts.
    /// <summary>Checks deadline partitions, the one-hundred-row batch limit and retained session revocation.</summary>
    [PostgresEndToEndFact]
    public async Task ExpiresOnlyDueAccountsInBoundedBatches()
    {
        await factory.ResetDatabaseAsync();
        await factory.ExecuteDbAsync(async context =>
        {
            var users = Enumerable.Range(0, 101).Select(i => Account($"due-{i}", UserStatus.Suspended, i == 0 ? Now : Now.AddSeconds(-1))).ToArray();
            context.Users.AddRange(users);
            context.Users.AddRange(Account("future", UserStatus.Suspended, Now.AddSeconds(1)),
                Account("indefinite", UserStatus.Suspended, null), Account("banned", UserStatus.Banned, Now.AddSeconds(-1)),
                Account("deleted", UserStatus.Suspended, Now.AddSeconds(-1), deleted: true));
            await context.SaveChangesAsync();
            context.UserSessions.Add(new UserSession { UserId = users[0].Id, RefreshTokenHash = "revoked-session", RevokedAt = Now.AddDays(-1), ExpiresAt = Now.AddDays(30) });
            await context.SaveChangesAsync();
        });
        (await ExpireAsync()).Should().Be(100);
        (await ExpireAsync()).Should().Be(1);
        (await ExpireAsync()).Should().Be(0);
        await factory.ExecuteDbAsync(async context =>
        {
            (await context.Users.CountAsync(u => u.Status == UserStatus.Active)).Should().Be(101);
            (await context.Users.CountAsync(u => u.Status == UserStatus.Active && u.SuspendedUntil != null)).Should().Be(0);
            (await context.Users.CountAsync(u => u.Status == UserStatus.Suspended)).Should().Be(2);
            (await context.Users.SingleAsync(u => u.Email == "banned@expiry.test")).Status.Should().Be(UserStatus.Banned);
            (await context.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == "deleted@expiry.test")).Status.Should().Be(UserStatus.Suspended);
            (await context.UserSessions.SingleAsync()).RevokedAt.Should().Be(Now.AddDays(-1));
        });
    }

    // TEST-02: Expiry skips an account locked by a concurrent sanction, leaving the subsequent ban intact.
    /// <summary>Verifies SKIP LOCKED prevents automatic expiry from overwriting a concurrent moderator decision.</summary>
    [PostgresEndToEndFact]
    public async Task DoesNotOverwriteConcurrentBan()
    {
        await factory.ResetDatabaseAsync();
        var id = await factory.ExecuteDbAsync(async context =>
        {
            var user = Account("locked", UserStatus.Suspended, Now);
            context.Users.Add(user);
            await context.SaveChangesAsync();
            return user.Id;
        });
        using var scope = factory.Services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(default);
        var user = await scope.ServiceProvider.GetRequiredService<IRestaurantModerationRepository>().LockUserAsync(id, default);
        (await ExpireAsync()).Should().Be(0);
        user!.Status = UserStatus.Banned;
        user.SuspendedUntil = null;
        await unitOfWork.SaveChangesAsync(default);
        await transaction.CommitAsync(default);
        (await ExpireAsync()).Should().Be(0);
        await factory.ExecuteDbAsync(async context => (await context.Users.SingleAsync()).Status.Should().Be(UserStatus.Banned));
    }

    /// <summary>Builds a non-staff account with a selected deadline and soft-delete state.</summary>
    private static User Account(string name, UserStatus status, DateTime? until, bool deleted = false) => new()
    {
        Email = name + "@expiry.test", DisplayName = name, RoleId = 1, EmailVerifiedAt = Now,
        Status = status, SuspendedUntil = until, DeletedAt = deleted ? Now : null
    };

    /// <summary>Runs a real scoped expiry transaction with a deterministic UTC boundary.</summary>
    private async Task<int> ExpireAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await new ExpireUserSuspensionsUseCase(scope.ServiceProvider.GetRequiredService<IRestaurantModerationRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), scope.ServiceProvider.GetRequiredService<IAccountStatusCache>(), new FixedClock()).ExecuteAsync(default);
    }

    /// <summary>Provides an exact UTC clock for deadline equality assertions.</summary>
    private sealed class FixedClock : TimeProvider
    {
        /// <summary>Returns the deterministic expiry instant.</summary>
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
