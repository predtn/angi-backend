using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Auth.Refresh
{
    /// <summary>Coordinates refresh-token validation, revocation, and rotation.</summary>
    public sealed class RefreshTokenUseCase : IRefreshTokenUseCase
    {
        private const string TravelerRole = "TRAVELER";
        private const string InvalidTokenCode = "REFRESH_TOKEN_INVALID";
        private const string InvalidTokenMessage = "Refresh token không hợp lệ hoặc đã hết hạn.";

        /// <summary>
        /// How long a token that was just rotated may be sent again. The frontend runs on several instances, so two
        /// requests whose access token expired together can both refresh with the same token; the second one gets its
        /// own valid pair instead of being treated as theft (API Design AUTH-06).
        /// </summary>
        public static readonly TimeSpan ReuseGracePeriod = TimeSpan.FromSeconds(30);

        private readonly IValidator<RefreshTokenRequestDto> _validator;
        private readonly IAuthenticationRepository _authenticationRepository;
        private readonly IAuthenticationTokenService _tokenService;
        private readonly IRecommendationService _recommendationService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _timeProvider;

        /// <summary>Initializes the use case with transaction, repository, token, and Recommendation Service abstractions.</summary>
        public RefreshTokenUseCase(
            IValidator<RefreshTokenRequestDto> validator,
            IAuthenticationRepository authenticationRepository,
            IAuthenticationTokenService tokenService,
            IRecommendationService recommendationService,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider)
        {
            _validator = validator;
            _authenticationRepository = authenticationRepository;
            _tokenService = tokenService;
            _recommendationService = recommendationService;
            _unitOfWork = unitOfWork;
            _timeProvider = timeProvider;
        }

        /// <summary>
        /// Locks the session in a transaction, handles token reuse, and atomically issues a new token pair.
        /// </summary>
        public async Task<AuthResultDto> ExecuteAsync(RefreshTokenRequestDto request, CancellationToken ct)
        {
            // STEP-01: Validate the request.
            await _validator.ValidateAndThrowAsync(request, ct);

            // STEP-02: Prepare refreshTokenHash through tokenService.HashRefreshToken.
            var refreshTokenHash = _tokenService.HashRefreshToken(request.RefreshToken);
            // STEP-03: Open the database transaction.
            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            // STEP-04: Load session through authenticationRepository.GetSessionByRefreshTokenHashForUpdateAsync.
            var session = await _authenticationRepository.GetSessionByRefreshTokenHashForUpdateAsync(refreshTokenHash, ct);
            // STEP-05: Prepare now from timeProvider.GetUtcNow.
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // STEP-06: Throw the error from InvalidRefreshToken when session is missing.
            if (session is null)
            {
                throw InvalidRefreshToken();
            }

            // STEP-07: When the session was rotated within the grace period, refuse a logged-out or blocked login, otherwise issue a parallel session.
            if (session.RevokedAt.HasValue && IsRecentRotation(session, now))
            {
                if (!IsUsableSuccessor(session.ReplacedBySession!, now) || session.User.Status != UserStatus.Active)
                {
                    // Rotated, then logged out or blocked: refuse without treating it as theft.
                    throw InvalidRefreshToken();
                }

                // A concurrent refresh already rotated this token: issue a parallel session and keep the first one.
                return await IssueSessionAsync(session, now, transaction, rotate: false, ct);
            }

            // STEP-08: When session.RevokedAt has a value, call RevokeAllActiveSessionsAsync, call transaction.CommitAsync and 1 more action(s).
            if (session.RevokedAt.HasValue)
            {
                await RevokeAllActiveSessionsAsync(session.UserId, now, ct);
                await transaction.CommitAsync(ct);
                throw InvalidRefreshToken();
            }

            // STEP-09: When session.ExpiresAt <= now or session.User.Status is not UserStatus.Active, run the branch below.
            if (session.ExpiresAt <= now || session.User.Status != UserStatus.Active)
            {
                // STEP-10: Update session: RevokedAt.
                session.RevokedAt = now;
                // STEP-11: Save the pending changes.
                await _unitOfWork.SaveChangesAsync(ct);
                // STEP-12: Commit the transaction.
                await transaction.CommitAsync(ct);
                // STEP-13: Throw the error from InvalidRefreshToken.
                throw InvalidRefreshToken();
            }

            // STEP-14: Rotate the session and return the new token pair through IssueSessionAsync.
            return await IssueSessionAsync(session, now, transaction, rotate: true, ct);
        }

        /// <summary>
        /// Adds a new session for the same user and device and returns its token pair; when rotating, the current session
        /// is revoked and linked to the new one so a late duplicate of its token can be recognised.
        /// </summary>
        private async Task<AuthResultDto> IssueSessionAsync(
            UserSession session,
            DateTime now,
            IUnitOfWorkTransaction transaction,
            bool rotate,
            CancellationToken ct)
        {
            // STEP-01: Prepare refreshToken through tokenService.CreateRefreshToken and newSession as UserSession.
            var refreshToken = _tokenService.CreateRefreshToken();
            var newSession = new UserSession
            {
                UserId = session.UserId,
                RefreshTokenHash = refreshToken.TokenHash,
                UserAgent = session.UserAgent,
                IpAddress = session.IpAddress,
                ExpiresAt = refreshToken.ExpiresAt
            };
            // STEP-02: Queue newSession through authenticationRepository.AddSession; when rotating, revoke session and link it to newSession.
            _authenticationRepository.AddSession(newSession);
            if (rotate)
            {
                session.RevokedAt = now;
                session.ReplacedBySession = newSession;
            }

            // STEP-03: Save the pending changes.
            await _unitOfWork.SaveChangesAsync(ct);
            // STEP-04: Commit the transaction.
            await transaction.CommitAsync(ct);

            // STEP-05: Prepare accessToken and tokens, then load needsPreferenceSurvey through GetNeedsPreferenceSurveyAsync.
            // Rotation creates a new session row, so the new access token carries its id as claim sid.
            var accessToken = _tokenService.CreateAccessToken(session.User, newSession.Id);
            var tokens = new AuthenticationTokens(
                accessToken.Token,
                accessToken.ExpiresAt,
                refreshToken.Token,
                refreshToken.TokenHash,
                refreshToken.ExpiresAt);

            var needsPreferenceSurvey = await GetNeedsPreferenceSurveyAsync(session.User, ct);
            // STEP-06: Return the result of AuthenticationResponseMapper.Map.
            return AuthenticationResponseMapper.Map(session.User, tokens, needsPreferenceSurvey);
        }

        /// <summary>True when the session was revoked by a refresh (not a logout) within the grace period.</summary>
        private static bool IsRecentRotation(UserSession session, DateTime now)
        {
            return session.ReplacedBySession is not null &&
                   session.RevokedAt!.Value > now - ReuseGracePeriod;
        }

        /// <summary>
        /// The successor still stands for this login: active, or itself rotated (a newer refresh, not a logout), and not expired.
        /// </summary>
        private static bool IsUsableSuccessor(UserSession successor, DateTime now)
        {
            return successor.ExpiresAt > now &&
                   (!successor.RevokedAt.HasValue || successor.ReplacedBySessionId.HasValue);
        }

        /// <summary>Revokes every active session when reuse of an old refresh token is detected.</summary>
        private async Task RevokeAllActiveSessionsAsync(int userId, DateTime now, CancellationToken ct)
        {
            // STEP-01: Load activeSessions through authenticationRepository.GetActiveSessionsAsync.
            var activeSessions = await _authenticationRepository.GetActiveSessionsAsync(userId, ct);
            // STEP-02: For each activeSession in activeSessions, set activeSession.RevokedAt.
            foreach (var activeSession in activeSessions)
            {
                activeSession.RevokedAt = now;
            }

            // STEP-03: When activeSessions.Count > 0, call unitOfWork.SaveChangesAsync.
            if (activeSessions.Count > 0)
            {
                await _unitOfWork.SaveChangesAsync(ct);
            }
        }

        /// <summary>Gets the Traveler survey status so refresh returns the same user shape as login.</summary>
        private async Task<bool?> GetNeedsPreferenceSurveyAsync(User user, CancellationToken ct)
        {
            // STEP-01: Return null early when user.Role.Code differs from TravelerRole.
            if (!string.Equals(user.Role.Code, TravelerRole, StringComparison.Ordinal))
            {
                return null;
            }

            // STEP-02: Load status through recommendationService.GetSurveyStatusAsync.
            var status = await _recommendationService.GetSurveyStatusAsync(user.Id, ct);
            // STEP-03: Return one of two results depending on status is missing.
            return status is null ? null : !status.Completed;
        }

        /// <summary>Creates the standard AUTH-06 exception without duplicating its error code and message.</summary>
        private static UnauthorizedException InvalidRefreshToken()
        {
            return new UnauthorizedException(InvalidTokenCode, InvalidTokenMessage);
        }
    }
}
