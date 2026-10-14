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
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Auth.GoogleLogin
{
    /// <summary>Implements AUTH-05 by verifying, linking, and signing in a Google identity.</summary>
    public sealed class GoogleLoginUseCase : IGoogleLoginUseCase
    {
        private const string TravelerRole = "TRAVELER";

        private readonly IValidator<GoogleLoginRequestDto> _validator;
        private readonly IGoogleIdentityService _googleIdentityService;
        private readonly IAuthenticationRepository _authenticationRepository;
        private readonly IAuthenticationTokenService _tokenService;
        private readonly IRecommendationService _recommendationService;
        private readonly ICurrentUserService _currentUserService;
        private readonly IAccountStatusCache _accountStatusCache;
        private readonly IAuditLogService _auditLogService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _timeProvider;

        /// <summary>Initializes the Google login workflow and all of its validation and persistence boundaries.</summary>
        public GoogleLoginUseCase(
            IValidator<GoogleLoginRequestDto> validator,
            IGoogleIdentityService googleIdentityService,
            IAuthenticationRepository authenticationRepository,
            IAuthenticationTokenService tokenService,
            IRecommendationService recommendationService,
            ICurrentUserService currentUserService,
            IAccountStatusCache accountStatusCache,
            IAuditLogService auditLogService,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider)
        {
            _validator = validator;
            _googleIdentityService = googleIdentityService;
            _authenticationRepository = authenticationRepository;
            _tokenService = tokenService;
            _recommendationService = recommendationService;
            _currentUserService = currentUserService;
            _accountStatusCache = accountStatusCache;
            _auditLogService = auditLogService;
            _unitOfWork = unitOfWork;
            _timeProvider = timeProvider;
        }

        /// <summary>Validates Google identity, creates or links an account atomically, and establishes a session.</summary>
        public async Task<GoogleLoginResponseDto> ExecuteAsync(
            GoogleLoginRequestDto request,
            CancellationToken ct)
        {
            await _validator.ValidateAndThrowAsync(request, ct);

            var identity = await _googleIdentityService.VerifyIdTokenAsync(request.IdToken, ct);
            var normalizedEmail = identity.Email.Trim().ToLowerInvariant();
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            await _authenticationRepository.LockExternalLoginAsync(
                ExternalLoginProvider.Google,
                identity.ProviderUserId,
                ct);

            var user = await _authenticationRepository.GetUserByExternalLoginAsync(
                ExternalLoginProvider.Google,
                identity.ProviderUserId,
                ct);
            var isNewUser = false;
            var statusChanged = false;

            if (user is null)
            {
                user = await _authenticationRepository.GetUserByEmailAsync(normalizedEmail, ct);
                if (user is null)
                {
                    if (request.Role is null)
                    {
                        throw new BadRequestException(
                            "ROLE_REQUIRED",
                            "Vai trò là bắt buộc khi đăng nhập Google lần đầu.");
                    }

                    var role = await _authenticationRepository.GetRoleByCodeAsync(request.Role, ct)
                        ?? throw new InvalidOperationException($"Configured role '{request.Role}' was not found.");
                    var userId = await _authenticationRepository.ReserveUserIdAsync(ct);
                    user = new User
                    {
                        Id = userId,
                        Email = normalizedEmail,
                        RoleId = role.Id,
                        Role = role,
                        DisplayName = CreateDisplayName(identity.DisplayName, normalizedEmail),
                        EmailVerifiedAt = now,
                        Status = UserStatus.Active
                    };
                    _authenticationRepository.AddUser(user);
                    isNewUser = true;
                }
                else
                {
                    EnsureGoogleLoginAllowed(user);
                    if (user.EmailVerifiedAt is null)
                    {
                        user.EmailVerifiedAt = now;
                    }

                    if (user.Status == UserStatus.PendingVerification)
                    {
                        user.Status = UserStatus.Active;
                        statusChanged = true;
                    }
                }

                _authenticationRepository.AddExternalLogin(new UserExternalLogin
                {
                    User = user,
                    Provider = ExternalLoginProvider.Google,
                    ProviderUserId = identity.ProviderUserId
                });
            }
            else
            {
                EnsureGoogleLoginAllowed(user);
            }

            var refreshToken = _tokenService.CreateRefreshToken();
            user.LastLoginAt = now;
            var session = new UserSession
            {
                User = user,
                RefreshTokenHash = refreshToken.TokenHash,
                UserAgent = _currentUserService.UserAgent,
                IpAddress = _currentUserService.IpAddress,
                ExpiresAt = refreshToken.ExpiresAt
            };
            _authenticationRepository.AddSession(session);
            if (isNewUser)
            {
                _auditLogService.Add(new AuditLogEntry
                {
                    Action = AuditActions.UserRegistered,
                    EntityType = AuditEntityTypes.User,
                    EntityId = user.Id,
                    SubjectUserId = user.Id,
                    ActorId = user.Id,
                    ActorRole = user.Role.Code
                });
            }

            _auditLogService.Add(new AuditLogEntry
            {
                Action = AuditActions.UserLogin,
                EntityType = AuditEntityTypes.User,
                EntityId = user.Id,
                SubjectUserId = user.Id,
                ActorId = user.Id,
                ActorRole = user.Role.Code
            });

            await _unitOfWork.SaveChangesAsync(ct);

            // The session id exists only once saved; the access token carries it as claim sid.
            var accessToken = _tokenService.CreateAccessToken(user, session.Id);
            var tokens = new AuthenticationTokens(
                accessToken.Token,
                accessToken.ExpiresAt,
                refreshToken.Token,
                refreshToken.TokenHash,
                refreshToken.ExpiresAt);
            await transaction.CommitAsync(ct);

            if (statusChanged)
            {
                _accountStatusCache.Invalidate(user.Id);
            }

            var needsPreferenceSurvey = await GetNeedsPreferenceSurveyAsync(user, ct);
            return AuthenticationResponseMapper.MapGoogle(user, tokens, needsPreferenceSurvey, isNewUser);
        }

        /// <summary>Allows verified Google email to activate pending accounts while rejecting blocked accounts.</summary>
        private static void EnsureGoogleLoginAllowed(User user)
        {
            if (user.Status == UserStatus.PendingVerification)
            {
                return;
            }

            AccountStatusRules.EnsureActive(user.Status, user.SuspendedUntil);
        }

        /// <summary>Builds a database-safe display name from an optional Google profile name.</summary>
        private static string CreateDisplayName(string? googleName, string normalizedEmail)
        {
            var displayName = string.IsNullOrWhiteSpace(googleName)
                ? normalizedEmail.Split('@', 2)[0]
                : googleName.Trim();
            return displayName.Length <= 100 ? displayName : displayName[..100];
        }

        /// <summary>Queries survey state only for Travelers and preserves login when Reco is unavailable.</summary>
        private async Task<bool?> GetNeedsPreferenceSurveyAsync(User user, CancellationToken ct)
        {
            if (!string.Equals(user.Role.Code, TravelerRole, StringComparison.Ordinal))
            {
                return null;
            }

            var status = await _recommendationService.GetSurveyStatusAsync(user.Id, ct);
            return status is null ? null : !status.Completed;
        }
    }
}
