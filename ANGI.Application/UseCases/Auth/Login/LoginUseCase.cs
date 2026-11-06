using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Domain.Entities;
using FluentValidation;

namespace ANGI.Application.UseCases.Auth.Login
{
    /// <summary>Coordinates the complete email-and-password login operation.</summary>
    public sealed class LoginUseCase : ILoginUseCase
    {
        private const string TravelerRole = "TRAVELER";

        private readonly IValidator<LoginRequestDto> _validator;
        private readonly IAuthenticationRepository _authenticationRepository;
        private readonly IPasswordService _passwordService;
        private readonly IAuthenticationTokenService _tokenService;
        private readonly IRecommendationService _recommendationService;
        private readonly ICurrentUserService _currentUserService;
        private readonly IAuditLogService _auditLogService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _timeProvider;

        /// <summary>Initializes the use case with the abstractions required for validation, persistence, and token issuance.</summary>
        public LoginUseCase(
            IValidator<LoginRequestDto> validator,
            IAuthenticationRepository authenticationRepository,
            IPasswordService passwordService,
            IAuthenticationTokenService tokenService,
            IRecommendationService recommendationService,
            ICurrentUserService currentUserService,
            IAuditLogService auditLogService,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider)
        {
            _validator = validator;
            _authenticationRepository = authenticationRepository;
            _passwordService = passwordService;
            _tokenService = tokenService;
            _recommendationService = recommendationService;
            _currentUserService = currentUserService;
            _auditLogService = auditLogService;
            _unitOfWork = unitOfWork;
            _timeProvider = timeProvider;
        }

        /// <summary>
        /// Validates the request, credentials, and account status, then creates a session and AuthResultDto.
        /// </summary>
        public async Task<AuthResultDto> ExecuteAsync(LoginRequestDto request, CancellationToken ct)
        {
            // STEP-01: Validate the request.
            await _validator.ValidateAndThrowAsync(request, ct);

            // STEP-02: Prepare normalizedEmail, then load user through authenticationRepository.GetUserByEmailAsync.
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var user = await _authenticationRepository.GetUserByEmailAsync(normalizedEmail, ct);

            // STEP-03: Reject with INVALID_CREDENTIALS when user.PasswordHash is missing or _passwordService.Verify(request.Password, user.PasswordHash) does not hold.
            if (user?.PasswordHash is null || !_passwordService.Verify(request.Password, user.PasswordHash))
            {
                throw new UnauthorizedException("INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng.");
            }

            // STEP-04: Check the active rule through AccountStatusRules.EnsureActive.
            AccountStatusRules.EnsureActive(user.Status, user.SuspendedUntil);

            // STEP-05: Prepare now from timeProvider.GetUtcNow and refreshToken through tokenService.CreateRefreshToken.
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var refreshToken = _tokenService.CreateRefreshToken();

            // STEP-06: Update user: LastLoginAt.
            user.LastLoginAt = now;
            // STEP-07: Prepare session as UserSession.
            var session = new UserSession
            {
                UserId = user.Id,
                RefreshTokenHash = refreshToken.TokenHash,
                UserAgent = _currentUserService.UserAgent,
                IpAddress = _currentUserService.IpAddress,
                ExpiresAt = refreshToken.ExpiresAt
            };
            // STEP-08: Queue session through authenticationRepository.AddSession.
            _authenticationRepository.AddSession(session);
            // STEP-09: Record audit action AuditActions.UserLogin.
            // AUTH-04 has no token yet, so the actor is the user who just logged in.
            _auditLogService.Add(new AuditLogEntry
            {
                Action = AuditActions.UserLogin,
                EntityType = AuditEntityTypes.User,
                EntityId = user.Id,
                SubjectUserId = user.Id,
                ActorId = user.Id,
                ActorRole = user.Role.Code
            });

            // STEP-10: Save the pending changes.
            await _unitOfWork.SaveChangesAsync(ct);

            // STEP-11: Prepare accessToken and tokens, then load needsPreferenceSurvey through GetNeedsPreferenceSurveyAsync.
            // The session id exists only once saved; the access token carries it as claim sid.
            var accessToken = _tokenService.CreateAccessToken(user, session.Id);
            var tokens = new AuthenticationTokens(
                accessToken.Token,
                accessToken.ExpiresAt,
                refreshToken.Token,
                refreshToken.TokenHash,
                refreshToken.ExpiresAt);

            var needsPreferenceSurvey = await GetNeedsPreferenceSurveyAsync(user, ct);
            // STEP-12: Return the result of AuthenticationResponseMapper.Map.
            return AuthenticationResponseMapper.Map(user, tokens, needsPreferenceSurvey);
        }

        /// <summary>
        /// Queries the Recommendation Service only for Travelers; other roles or Reco failures return null.
        /// </summary>
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
    }
}
