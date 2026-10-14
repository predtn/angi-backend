using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Account;

/// <summary>Provides persistence operations required by the Account use cases.</summary>
public interface IAccountRepository
{
    /// <summary>Finds a user by ID for reading and updating password/profile.</summary>
    Task<User?> GetUserByIdAsync(int userId, CancellationToken ct);

    /// <summary>Gets active sessions of a user that have not been revoked.</summary>
    Task<IReadOnlyList<UserSession>> GetActiveSessionsAsync(int userId, CancellationToken ct);
}
