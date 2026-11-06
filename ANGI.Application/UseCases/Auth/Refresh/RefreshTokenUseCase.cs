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
            await _validator.ValidateAndThrowAsync(request, ct);

            var refreshTokenHash = _tokenService.HashRefreshToken(request.RefreshToken);
            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            var session = await _authenticationRepository.GetSessionByRefreshTokenHashForUpdateAsync(refreshTokenHash, ct);
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            if (session is null)
            {
                throw InvalidRefreshToken();
            }

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

            if (session.RevokedAt.HasValue)
            {
                await RevokeAllActiveSessionsAsync(session.UserId, now, ct);
                await transaction.CommitAsync(ct);
                throw InvalidRefreshToken();
            }

            if (session.ExpiresAt <= now || session.User.Status != UserStatus.Active)
            {
                session.RevokedAt = now;
                await _unitOfWork.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                throw InvalidRefreshToken();
            }

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
            var refreshToken = _tokenService.CreateRefreshToken();
            var newSession = new UserSession
            {
                UserId = session.UserId,
                RefreshTokenHash = refreshToken.TokenHash,
                UserAgent = session.UserAgent,
                IpAddress = session.IpAddress,
                ExpiresAt = refreshToken.ExpiresAt
            };
            _authenticationRepository.AddSession(newSession);
            if (rotate)
            {
                session.RevokedAt = now;
                session.ReplacedBySession = newSession;
            }

            await _unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            // Rotation creates a new session row, so the new access token carries its id as claim sid.
            var accessToken = _tokenService.CreateAccessToken(session.User, newSession.Id);
            var tokens = new AuthenticationTokens(
                accessToken.Token,
                accessToken.ExpiresAt,
                refreshToken.Token,
                refreshToken.TokenHash,
                refreshToken.ExpiresAt);

            var needsPreferenceSurvey = await GetNeedsPreferenceSurveyAsync(session.User, ct);
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
            var activeSessions = await _authenticationRepository.GetActiveSessionsAsync(userId, ct);
            foreach (var activeSession in activeSessions)
            {
                activeSession.RevokedAt = now;
            }

            if (activeSessions.Count > 0)
            {
                await _unitOfWork.SaveChangesAsync(ct);
            }
        }

        /// <summary>Gets the Traveler survey status so refresh returns the same user shape as login.</summary>
        private async Task<bool?> GetNeedsPreferenceSurveyAsync(User user, CancellationToken ct)
        {
            if (!string.Equals(user.Role.Code, TravelerRole, StringComparison.Ordinal))
            {
                return null;
            }

            var status = await _recommendationService.GetSurveyStatusAsync(user.Id, ct);
            return status is null ? null : !status.Completed;
        }

        /// <summary>Creates the standard AUTH-06 exception without duplicating its error code and message.</summary>
        private static UnauthorizedException InvalidRefreshToken()
        {
            return new UnauthorizedException(InvalidTokenCode, InvalidTokenMessage);
        }
    }
}
