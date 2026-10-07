namespace ANGI.Application.Common.Interfaces.Repositories
{
    /// <summary>
    /// Coordinates transactions and persists all changes made by a use case.
    /// </summary>
    public interface IUnitOfWork
    {
        /// <summary>
        /// Starts a transaction so multiple read and write operations are processed atomically.
        /// </summary>
        Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct);

        /// <summary>
        /// Persists all entities currently tracked by the DbContext in one operation.
        /// </summary>
        Task<int> SaveChangesAsync(CancellationToken ct);
    }
}
