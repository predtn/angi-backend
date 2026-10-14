using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Models.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
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

        /// <summary>Loads the user associated with the provider subject and the data needed by AuthResultDto.</summary>
        public Task<User?> GetUserByExternalLoginAsync(
            ExternalLoginProvider provider,
            string providerUserId,
            CancellationToken ct)
        {
            return _context.Users
                .Include(user => user.Role)
                .Include(user => user.AvatarMedia)
                .Where(user => _context.UserExternalLogins.Any(login =>
                    login.UserId == user.Id &&
                    login.Provider == provider &&
                    login.ProviderUserId == providerUserId))
                .FirstOrDefaultAsync(ct);
        }

        /// <summary>Acquires a transaction-scoped PostgreSQL advisory lock for one provider subject.</summary>
        public async Task LockExternalLoginAsync(
            ExternalLoginProvider provider,
            string providerUserId,
            CancellationToken ct)
        {
            var lockKey = $"{provider}:{providerUserId}";
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
                ct);
        }

        /// <summary>Checks the partial unique-email domain; the global filter excludes soft-deleted users.</summary>
        public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct)
        {
            return _context.Users
                .AsNoTracking()
                .AnyAsync(user => user.Email == normalizedEmail, ct);
        }

        /// <summary>Loads only the role row identified by its stable API code.</summary>
        public Task<Role?> GetRoleByCodeAsync(string roleCode, CancellationToken ct)
        {
            return _context.Roles
                .AsNoTracking()
                .FirstOrDefaultAsync(role => role.Code == roleCode, ct);
        }

        /// <summary>Gets the next users sequence value for single-save account and session creation.</summary>
        public Task<int> ReserveUserIdAsync(CancellationToken ct)
        {
            return _context.Database
                .SqlQueryRaw<int>("SELECT nextval('core.users_id_seq')::integer AS \"Value\"")
                .SingleAsync(ct);
        }

        /// <summary>Loads and locks a user so concurrent resend requests replace tokens in sequence.</summary>
        public Task<User?> GetUserByEmailForUpdateAsync(string normalizedEmail, CancellationToken ct)
        {
            return _context.Users
                .FromSqlInterpolated($$"""
                    SELECT *
                    FROM core.users
                    WHERE email = {{normalizedEmail}}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(ct);
        }

        /// <summary>Projects the status fields of a user; the soft-delete filter hides deleted users.</summary>
        public Task<AccountStatusSnapshot?> GetAccountStatusAsync(int userId, CancellationToken ct)
        {
            return _context.Users
                .AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => new AccountStatusSnapshot(user.Status, user.SuspendedUntil))
                .FirstOrDefaultAsync(ct);
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

        /// <summary>Marks a new account as Added without persisting it immediately.</summary>
        public void AddUser(User user)
        {
            if (user.Role is not null && _context.Entry(user.Role).State == EntityState.Detached)
            {
                // Google registration needs the role navigation immediately for JWT/response mapping.
                // The role came from an AsNoTracking lookup and must never be inserted as a new role.
                _context.Roles.Attach(user.Role);
            }

            _context.Users.Add(user);
        }

        /// <summary>Marks an external identity link as Added without persisting it immediately.</summary>
        public void AddExternalLogin(UserExternalLogin externalLogin)
        {
            _context.UserExternalLogins.Add(externalLogin);
        }

        /// <summary>Loads and locks a user token together with its owner to enforce one-time consumption.</summary>
        public Task<UserToken?> GetUserTokenByHashForUpdateAsync(string tokenHash, CancellationToken ct)
        {
            return _context.UserTokens
                .FromSqlInterpolated($$"""
                    SELECT *
                    FROM core.user_tokens
                    WHERE token_hash = {{tokenHash}}
                    FOR UPDATE
                    """)
                .Include(token => token.User)
                .SingleOrDefaultAsync(ct);
        }

        /// <summary>Loads all unconsumed tokens for the selected user and purpose.</summary>
        public async Task<IReadOnlyList<UserToken>> GetUnusedUserTokensAsync(
            int userId,
            UserTokenPurpose purpose,
            CancellationToken ct)
        {
            return await _context.UserTokens
                .Where(token => token.UserId == userId &&
                                token.Purpose == purpose &&
                                token.UsedAt == null)
                .ToListAsync(ct);
        }

        /// <summary>Marks a new user token as Added without persisting it immediately.</summary>
        public void AddUserToken(UserToken token)
        {
            _context.UserTokens.Add(token);
        }
    }
}
