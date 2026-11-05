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

            // STEP-07: When session.RevokedAt has a value, call RevokeAllActiveSessionsAsync, call transaction.CommitAsync and 1 more action(s).
            if (session.RevokedAt.HasValue)
            {
                await RevokeAllActiveSessionsAsync(session.UserId, now, ct);
                await transaction.CommitAsync(ct);
                throw InvalidRefreshToken();
            }

            // STEP-08: When session.ExpiresAt <= now or session.User.Status is not UserStatus.Active, run the branch below.
            if (session.ExpiresAt <= now || session.User.Status != UserStatus.Active)
            {
                // STEP-09: Update session: RevokedAt.
                session.RevokedAt = now;
                // STEP-10: Save the pending changes.
                await _unitOfWork.SaveChangesAsync(ct);
                // STEP-11: Commit the transaction.
                await transaction.CommitAsync(ct);
                // STEP-12: Throw the error from InvalidRefreshToken.
                throw InvalidRefreshToken();
            }

            // STEP-13: Update session: RevokedAt.
            session.RevokedAt = now;
            // STEP-14: Prepare refreshToken through tokenService.CreateRefreshToken and newSession as UserSession.
            var refreshToken = _tokenService.CreateRefreshToken();
            var newSession = new UserSession
            {
                UserId = session.UserId,
                RefreshTokenHash = refreshToken.TokenHash,
                UserAgent = session.UserAgent,
                IpAddress = session.IpAddress,
                ExpiresAt = refreshToken.ExpiresAt
            };
            // STEP-15: Queue newSession through authenticationRepository.AddSession.
            _authenticationRepository.AddSession(newSession);

            // STEP-16: Save the pending changes.
            await _unitOfWork.SaveChangesAsync(ct);
            // STEP-17: Commit the transaction.
            await transaction.CommitAsync(ct);

            // STEP-18: Prepare accessToken and tokens, then load needsPreferenceSurvey through GetNeedsPreferenceSurveyAsync.
            // Rotation creates a new session row, so the new access token carries its id as claim sid.
            var accessToken = _tokenService.CreateAccessToken(session.User, newSession.Id);
            var tokens = new AuthenticationTokens(
                accessToken.Token,
                accessToken.ExpiresAt,
                refreshToken.Token,
                refreshToken.TokenHash,
                refreshToken.ExpiresAt);

            var needsPreferenceSurvey = await GetNeedsPreferenceSurveyAsync(session.User, ct);
            // STEP-19: Return the result of AuthenticationResponseMapper.Map.
            return AuthenticationResponseMapper.Map(session.User, tokens, needsPreferenceSurvey);
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
