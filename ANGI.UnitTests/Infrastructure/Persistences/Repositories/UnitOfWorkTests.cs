using ANGI.Application.Common.Exceptions;
using ANGI.Domain.Entities;
using ANGI.Infrastructure.Persistences;
using ANGI.Infrastructure.Persistences.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;

namespace ANGI.UnitTests.Infrastructure.Persistences.Repositories;

/// <summary>Verifies database failures are translated to the correct Application error contract.</summary>
public sealed class UnitOfWorkTests
{
    // TEST-01: Translate a PostgreSQL unique constraint violation into the application exception.
    /// <summary>Preserves the violated constraint without exposing PostgreSQL types to callers.</summary>
    [Fact]
    public async Task SaveChangesAsync_WithPostgresUniqueViolation_ShouldHideProviderBehindApplicationException()
    {
        var postgresException = new PostgresException(
            "duplicate key value violates unique constraint",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            constraintName: "ix_users_email");
        var dbException = new DbUpdateException("Save failed.", postgresException);
        await using var context = new ThrowingContext(dbException);
        var unitOfWork = new UnitOfWork(context);

        var act = () => unitOfWork.SaveChangesAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UniqueConstraintViolationException>();
        exception.Which.ConstraintName.Should().Be("ix_users_email");
        exception.Which.InnerException.Should().BeSameAs(dbException);
    }

    // TEST-02: Preserve database failures that are not PostgreSQL unique constraint violations.
    /// <summary>Leaves unrelated database errors available for unexpected-error handling and logging.</summary>
    [Fact]
    public async Task SaveChangesAsync_WithNonUniqueDatabaseError_ShouldPreserveOriginalException()
    {
        var dbException = new DbUpdateException("Save failed.", new InvalidOperationException("database error"));
        await using var context = new ThrowingContext(dbException);
        var unitOfWork = new UnitOfWork(context);

        var act = () => unitOfWork.SaveChangesAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<DbUpdateException>();
        exception.Which.Should().BeSameAs(dbException);
    }

    // TEST-03: Report the conversation conflict for Agent rows and the roadmap conflict for other concurrency failures.
    /// <summary>Ensures clients reload the resource whose optimistic revision actually failed.</summary>
    [Theory]
    [InlineData(true, "CONVERSATION_VERSION_CONFLICT")]
    [InlineData(false, "ROADMAP_UPDATED_AT_CONFLICT")]
    public async Task SaveChangesAsync_WithConcurrencyFailure_IdentifiesConflictingResource(bool conversation, string code)
    {
        await using var model = new ANGIContext(new DbContextOptionsBuilder<ANGIContext>()
            .UseNpgsql("Host=localhost;Database=unit_test").UseSnakeCaseNamingConvention().Options, TimeProvider.System);
        object entity = conversation ? new AgentConversation { Id = 7 } : new ANGI.Domain.Entities.Roadmap { Id = 88 };
        var failure = new Mock<DbUpdateConcurrencyException>();
        failure.SetupGet(exception => exception.Entries).Returns([model.Entry(entity)]);
        await using var context = new ThrowingContext(failure.Object);

        var act = () => new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be(code);
    }

    /// <summary>Returns a predetermined persistence failure without connecting to a database.</summary>
    private sealed class ThrowingContext : ANGIContext
    {
        private readonly Exception _exception;

        /// <summary>Stores the failure that the Unit of Work must translate or preserve.</summary>
        public ThrowingContext(Exception exception)
            : base(new DbContextOptionsBuilder<ANGIContext>().Options, TimeProvider.System)
        {
            _exception = exception;
        }

        /// <summary>Fails asynchronously at the same boundary used by the production Unit of Work.</summary>
        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<int>(_exception);
        }
    }
}
