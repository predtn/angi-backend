using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
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
            await _validator.ValidateAndThrowAsync(request, ct);

            var refreshTokenHash = _tokenService.HashRefreshToken(request.RefreshToken);
            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            var session = await _authenticationRepository.GetSessionByRefreshTokenHashForUpdateAsync(refreshTokenHash, ct);
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            if (session is null)
            {
                throw InvalidRefreshToken();
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

            session.RevokedAt = now;
            var tokens = _tokenService.CreateTokens(session.User);
            _authenticationRepository.AddSession(new UserSession
            {
                UserId = session.UserId,
                RefreshTokenHash = tokens.RefreshTokenHash,
                UserAgent = session.UserAgent,
                IpAddress = session.IpAddress,
                ExpiresAt = tokens.RefreshTokenExpiresAt
            });

            await _unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            var needsPreferenceSurvey = await GetNeedsPreferenceSurveyAsync(session.User, ct);
            return AuthenticationResponseMapper.Map(session.User, tokens, needsPreferenceSurvey);
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

            var completed = await _recommendationService.GetSurveyCompletionAsync(user.Id, ct);
            return completed.HasValue ? !completed.Value : null;
        }

        /// <summary>Creates the standard AUTH-06 exception without duplicating its error code and message.</summary>
        private static UnauthorizedException InvalidRefreshToken()
        {
            return new UnauthorizedException(InvalidTokenCode, InvalidTokenMessage);
        }
    }
}
