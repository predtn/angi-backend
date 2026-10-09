using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ANGI.Infrastructure.Persistences.Repositories
{
    /// <summary>Provides the shared EF Core Unit of Work implementation for write use cases.</summary>
    public class UnitOfWork : IUnitOfWork
    {
        private readonly ANGIContext _context;

        /// <summary>Initializes the Unit of Work with the same scoped DbContext used by repositories.</summary>
        public UnitOfWork(ANGIContext context)
        {
            _context = context;
        }

        /// <summary>Persists all entity changes tracked by the scoped DbContext.</summary>
        public async Task<int> SaveChangesAsync(CancellationToken ct)
        {
            try
            {
                return await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation
                } postgresException)
            {
                throw new UniqueConstraintViolationException(postgresException.ConstraintName, exception);
            }
        }

        /// <summary>Starts an EF Core database transaction and wraps it in the Application-layer abstraction.</summary>
        public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct)
        {
            var transaction = await _context.Database.BeginTransactionAsync(ct);
            return new UnitOfWorkTransaction(transaction);
        }

        private sealed class UnitOfWorkTransaction : IUnitOfWorkTransaction
        {
            private readonly IDbContextTransaction _transaction;

            /// <summary>Keeps the EF Core transaction inside the Infrastructure layer.</summary>
            public UnitOfWorkTransaction(IDbContextTransaction transaction)
            {
                _transaction = transaction;
            }

            /// <summary>Commits the current transaction.</summary>
            public Task CommitAsync(CancellationToken ct)
            {
                return _transaction.CommitAsync(ct);
            }

            /// <summary>Disposes the transaction; the provider rolls it back when it has not been committed.</summary>
            public ValueTask DisposeAsync()
            {
                return _transaction.DisposeAsync();
            }
        }
    }
}
