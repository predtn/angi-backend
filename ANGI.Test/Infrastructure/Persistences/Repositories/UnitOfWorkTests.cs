using ANGI.Application.Common.Exceptions;
using ANGI.Infrastructure.Persistences;
using ANGI.Infrastructure.Persistences.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ANGI.Test.Infrastructure.Persistences.Repositories;

public sealed class UnitOfWorkTests
{
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

    private sealed class ThrowingContext : ANGIContext
    {
        private readonly Exception _exception;

        public ThrowingContext(Exception exception)
            : base(new DbContextOptionsBuilder<ANGIContext>().Options, TimeProvider.System)
        {
            _exception = exception;
        }

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<int>(_exception);
        }
    }
}
