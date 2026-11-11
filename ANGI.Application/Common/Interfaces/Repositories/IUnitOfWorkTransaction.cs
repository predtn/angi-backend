namespace ANGI.Application.Common.Interfaces.Repositories
{
    /// <summary>
    /// Represents the current Unit of Work transaction without exposing EF Core.
    /// Disposing an uncommitted transaction allows the implementation to roll it back.
    /// </summary>
    public interface IUnitOfWorkTransaction : IAsyncDisposable
    {
        /// <summary>
        /// Commits all changes in the current transaction.
        /// </summary>
        Task CommitAsync(CancellationToken ct);
    }
}
