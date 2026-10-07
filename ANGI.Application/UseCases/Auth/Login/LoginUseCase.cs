using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
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
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider)
        {
            _validator = validator;
            _authenticationRepository = authenticationRepository;
            _passwordService = passwordService;
            _tokenService = tokenService;
            _recommendationService = recommendationService;
            _currentUserService = currentUserService;
            _unitOfWork = unitOfWork;
            _timeProvider = timeProvider;
        }

        /// <summary>
        /// Validates the request, credentials, and account status, then creates a session and AuthResultDto.
        /// </summary>
        public async Task<AuthResultDto> ExecuteAsync(LoginRequestDto request, CancellationToken ct)
        {
            await _validator.ValidateAndThrowAsync(request, ct);

            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var user = await _authenticationRepository.GetUserByEmailAsync(normalizedEmail, ct);

            if (user?.PasswordHash is null || !_passwordService.Verify(request.Password, user.PasswordHash))
            {
                throw new UnauthorizedException("INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng.");
            }

            AccountStatusRules.EnsureActive(user.Status, user.SuspendedUntil);

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var tokens = _tokenService.CreateTokens(user);

            user.LastLoginAt = now;
            _authenticationRepository.AddSession(new UserSession
            {
                UserId = user.Id,
                RefreshTokenHash = tokens.RefreshTokenHash,
                UserAgent = _currentUserService.UserAgent,
                IpAddress = _currentUserService.IpAddress,
                ExpiresAt = tokens.RefreshTokenExpiresAt
            });
            _authenticationRepository.AddAuditLog(new AuditLog
            {
                ActorId = user.Id,
                ActorRole = user.Role.Code,
                Action = "USER_LOGIN",
                EntityType = "USER",
                EntityId = user.Id,
                SubjectUserId = user.Id,
                IpAddress = _currentUserService.IpAddress,
                UserAgent = _currentUserService.UserAgent
            });

            await _unitOfWork.SaveChangesAsync(ct);

            var needsPreferenceSurvey = await GetNeedsPreferenceSurveyAsync(user, ct);
            return AuthenticationResponseMapper.Map(user, tokens, needsPreferenceSurvey);
        }

        /// <summary>
        /// Queries the Recommendation Service only for Travelers; other roles or Reco failures return null.
        /// </summary>
        private async Task<bool?> GetNeedsPreferenceSurveyAsync(User user, CancellationToken ct)
        {
            if (!string.Equals(user.Role.Code, TravelerRole, StringComparison.Ordinal))
            {
                return null;
            }

            var completed = await _recommendationService.GetSurveyCompletionAsync(user.Id, ct);
            return completed.HasValue ? !completed.Value : null;
        }
    }
}
