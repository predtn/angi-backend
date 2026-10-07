using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Auth
{
    /// <summary>
    /// Provides the persistence operations required by the Auth use cases.
    /// The interface describes storage behavior while Infrastructure implements it with EF Core.
    /// </summary>
    public interface IAuthenticationRepository
    {
        /// <summary>Finds a user by normalized email and loads the data required for the authentication result.</summary>
        Task<User?> GetUserByEmailAsync(string normalizedEmail, CancellationToken ct);

        /// <summary>Finds a session by refresh-token hash for operations that do not require a row lock, such as logout.</summary>
        Task<UserSession?> GetSessionByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken ct);

        /// <summary>
        /// Finds and locks a session until the transaction ends, preventing concurrent refreshes with the same token.
        /// </summary>
        Task<UserSession?> GetSessionByRefreshTokenHashForUpdateAsync(string refreshTokenHash, CancellationToken ct);

        /// <summary>Gets the user sessions that have not been revoked so refresh-token reuse can be handled.</summary>
        Task<IReadOnlyList<UserSession>> GetActiveSessionsAsync(int userId, CancellationToken ct);

        /// <summary>Adds a new session to the change tracker; the Unit of Work persists it.</summary>
        void AddSession(UserSession session);

        /// <summary>Adds a new audit log to the change tracker; the Unit of Work persists it.</summary>
        void AddAuditLog(AuditLog auditLog);
    }
}
