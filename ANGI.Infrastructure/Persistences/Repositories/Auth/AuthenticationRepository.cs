using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Auth
{
    /// <summary>Provides the EF Core implementation of Auth persistence operations.</summary>
    public sealed class AuthenticationRepository : IAuthenticationRepository
    {
        private readonly ANGIContext _context;

        /// <summary>Initializes the repository with the scoped ANGIContext.</summary>
        public AuthenticationRepository(ANGIContext context)
        {
            _context = context;
        }

        /// <summary>Loads a user by email together with the Role and AvatarMedia required by AuthResultDto.</summary>
        public Task<User?> GetUserByEmailAsync(string normalizedEmail, CancellationToken ct)
        {
            return _context.Users
                .Include(user => user.Role)
                .Include(user => user.AvatarMedia)
                .FirstOrDefaultAsync(user => user.Email == normalizedEmail, ct);
        }

        /// <summary>Loads a session by refresh-token hash without a row lock; used by logout.</summary>
        public Task<UserSession?> GetSessionByRefreshTokenHashAsync(
            string refreshTokenHash,
            CancellationToken ct)
        {
            return _context.UserSessions
                .Include(session => session.User)
                    .ThenInclude(user => user.Role)
                .Include(session => session.User)
                    .ThenInclude(user => user.AvatarMedia)
                .FirstOrDefaultAsync(session => session.RefreshTokenHash == refreshTokenHash, ct);
        }

        /// <summary>
        /// Loads a session with SELECT FOR UPDATE to serialize refresh requests using the same token.
        /// </summary>
        public Task<UserSession?> GetSessionByRefreshTokenHashForUpdateAsync(
            string refreshTokenHash,
            CancellationToken ct)
        {
            return _context.UserSessions
                .FromSqlInterpolated($$"""
                    SELECT *
                    FROM core.user_sessions
                    WHERE refresh_token_hash = {{refreshTokenHash}}
                    FOR UPDATE
                    """)
                .Include(session => session.User)
                    .ThenInclude(user => user.Role)
                .Include(session => session.User)
                    .ThenInclude(user => user.AvatarMedia)
                .SingleOrDefaultAsync(ct);
        }

        /// <summary>Gets the user sessions without RevokedAt so they can be revoked when token reuse is detected.</summary>
        public async Task<IReadOnlyList<UserSession>> GetActiveSessionsAsync(int userId, CancellationToken ct)
        {
            return await _context.UserSessions
                .Where(session => session.UserId == userId && session.RevokedAt == null)
                .ToListAsync(ct);
        }

        /// <summary>Marks a new session as Added in the DbContext without writing it until SaveChangesAsync.</summary>
        public void AddSession(UserSession session)
        {
            _context.UserSessions.Add(session);
        }

        /// <summary>Marks a new audit log as Added in the DbContext without writing it until SaveChangesAsync.</summary>
        public void AddAuditLog(AuditLog auditLog)
        {
            _context.AuditLogs.Add(auditLog);
        }
    }
}
