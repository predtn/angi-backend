using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.ResendVerificationEmail;
using ANGI.Application.UseCases.Auth.VerifyEmail;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences;
using ANGI.Infrastructure.Persistences.Repositories;
using ANGI.Infrastructure.Persistences.Repositories.Auth;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;

namespace ANGI.Test.Infrastructure.Persistences.Auth;

public sealed class EmailVerificationPostgresConcurrencyTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc);

    // TEST-01: Serialize two real PostgreSQL consumers of the same verification token.
    [PostgresIntegrationFact]
    public async Task VerifyEmail_WithTwoDatabaseScopes_ShouldAllowExactlyOneConsumer()
    {
        var connectionString = RequiredConnectionString();
        var seed = await SeedPendingUserAsync(connectionString, "verify", "shared-token-hash");

        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = RunVerifyAsync(connectionString, seed.TokenHash, gate.Task);
            var second = RunVerifyAsync(connectionString, seed.TokenHash, gate.Task);
            gate.SetResult();

            var outcomes = await Task.WhenAll(first, second);

            outcomes.Should().ContainSingle(outcome => outcome == "success");
            outcomes.Should().ContainSingle(outcome => outcome == "TOKEN_ALREADY_USED");

            await using var assertionContext = CreateContext(connectionString);
            var token = await assertionContext.UserTokens.SingleAsync(item => item.Id == seed.TokenId);
            var user = await assertionContext.Users.SingleAsync(item => item.Id == seed.UserId);
            token.UsedAt.Should().Be(Now);
            user.Status.Should().Be(UserStatus.Active);
            user.EmailVerifiedAt.Should().Be(Now);
        }
        finally
        {
            await DeleteUserAsync(connectionString, seed.UserId);
        }
    }

    // TEST-02: Serialize resend token replacement across two independent application scopes.
    [PostgresIntegrationFact]
    public async Task ResendVerificationEmail_WithTwoDatabaseScopes_ShouldLeaveOneUnusedToken()
    {
        var connectionString = RequiredConnectionString();
        var seed = await SeedPendingUserAsync(connectionString, "resend", "old-token-hash");

        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = RunResendAsync(
                connectionString,
                seed.Email,
                new GeneratedUserToken("first-raw-token", $"first-{Guid.NewGuid():N}"),
                gate.Task);
            var second = RunResendAsync(
                connectionString,
                seed.Email,
                new GeneratedUserToken("second-raw-token", $"second-{Guid.NewGuid():N}"),
                gate.Task);
            gate.SetResult();

            await Task.WhenAll(first, second);

            await using var assertionContext = CreateContext(connectionString);
            var tokens = await assertionContext.UserTokens
                .Where(item => item.UserId == seed.UserId && item.Purpose == UserTokenPurpose.EmailVerify)
                .OrderBy(item => item.Id)
                .ToListAsync();

            tokens.Should().HaveCount(3);
            tokens.Should().ContainSingle(item => item.UsedAt == null);
            tokens.Single(item => item.Id == seed.TokenId).UsedAt.Should().NotBeNull();
        }
        finally
        {
            await DeleteUserAsync(connectionString, seed.UserId);
        }
    }

    private static async Task<string> RunVerifyAsync(
        string connectionString,
        string tokenHash,
        Task gate)
    {
        await using var context = CreateContext(connectionString);
        var repository = new AuthenticationRepository(context);
        var tokenService = new Mock<IAuthenticationTokenService>();
        tokenService.Setup(service => service.HashUserToken("shared-raw-token")).Returns(tokenHash);
        var useCase = new VerifyEmailUseCase(
            new VerifyEmailRequestDtoValidator(),
            repository,
            tokenService.Object,
            new Mock<IAccountStatusCache>().Object,
            new UnitOfWork(context),
            new FixedTimeProvider(Now));

        await gate;
        try
        {
            await useCase.ExecuteAsync(
                new VerifyEmailRequestDto { Token = "shared-raw-token" },
                CancellationToken.None);
            return "success";
        }
        catch (AppException exception)
        {
            return exception.ErrorCode;
        }
    }

    private static async Task RunResendAsync(
        string connectionString,
        string email,
        GeneratedUserToken generatedToken,
        Task gate)
    {
        await using var context = CreateContext(connectionString);
        var repository = new AuthenticationRepository(context);
        var tokenService = new Mock<IAuthenticationTokenService>();
        tokenService.Setup(service => service.CreateUserToken()).Returns(generatedToken);
        var emailService = new Mock<IEmailService>();
        var useCase = new ResendVerificationEmailUseCase(
            new ResendVerificationEmailRequestDtoValidator(),
            repository,
            tokenService.Object,
            emailService.Object,
            new UnitOfWork(context),
            new FixedTimeProvider(Now));

        await gate;
        await useCase.ExecuteAsync(
            new ResendVerificationEmailRequestDto { Email = email },
            CancellationToken.None);
        emailService.Verify(service => service.SendEmailVerificationAsync(
            email,
            It.IsAny<string>(),
            generatedToken.Token,
            TimeSpan.FromHours(24),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task<SeedResult> SeedPendingUserAsync(
        string connectionString,
        string prefix,
        string tokenHashPrefix)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Email = $"angi-58-{prefix}-{suffix}@integration.test",
            RoleId = 1,
            DisplayName = "ANGI 58 Integration",
            Status = UserStatus.PendingVerification
        };
        var token = new UserToken
        {
            User = user,
            Purpose = UserTokenPurpose.EmailVerify,
            TokenHash = $"{tokenHashPrefix}-{suffix}",
            ExpiresAt = Now.AddHours(1)
        };
        context.UserTokens.Add(token);
        await context.SaveChangesAsync();
        return new SeedResult(user.Id, user.Email, token.Id, token.TokenHash);
    }

    private static async Task DeleteUserAsync(string connectionString, int userId)
    {
        await using var context = CreateContext(connectionString);
        await context.Users.Where(user => user.Id == userId).ExecuteDeleteAsync();
    }

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

    private sealed record SeedResult(int UserId, string Email, long TokenId, string TokenHash);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTime now)
        {
            _now = new DateTimeOffset(now);
        }

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
