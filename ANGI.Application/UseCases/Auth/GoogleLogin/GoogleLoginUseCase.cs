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
            // STEP-01: Validate the request.
            await _validator.ValidateAndThrowAsync(request, ct);

            // STEP-02: Call googleIdentityService.VerifyIdTokenAsync and keep identity.
            var identity = await _googleIdentityService.VerifyIdTokenAsync(request.IdToken, ct);
            // STEP-03: Prepare normalizedEmail through identity.Email.Trim().ToLowerInvariant and now from timeProvider.GetUtcNow.
            var normalizedEmail = identity.Email.Trim().ToLowerInvariant();
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // STEP-04: Open the database transaction.
            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            // STEP-05: Load authenticationRepository.LockExternalLoginAsync and user through authenticationRepository.GetUserByExternalLoginAsync.
            await _authenticationRepository.LockExternalLoginAsync(
                ExternalLoginProvider.Google,
                identity.ProviderUserId,
                ct);

            var user = await _authenticationRepository.GetUserByExternalLoginAsync(
                ExternalLoginProvider.Google,
                identity.ProviderUserId,
                ct);
            // STEP-06: Prepare isNewUser and statusChanged.
            var isNewUser = false;
            var statusChanged = false;

            // STEP-07: When user is missing, set user, check whether user is missing and 1 more action(s); otherwise call EnsureGoogleLoginAllowed.
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
                        // Whoever registered this unverified email chose the password; Google proves the real owner,
                        // so that password and its sessions must not survive the link.
                        user.PasswordHash = null;
                        foreach (var activeSession in await _authenticationRepository.GetActiveSessionsAsync(user.Id, ct))
                        {
                            activeSession.RevokedAt = now;
                        }

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

            // STEP-08: Prepare refreshToken through tokenService.CreateRefreshToken.
            var refreshToken = _tokenService.CreateRefreshToken();
            // STEP-09: Update user: LastLoginAt.
            user.LastLoginAt = now;
            // STEP-10: Prepare session as UserSession.
            var session = new UserSession
            {
                User = user,
                RefreshTokenHash = refreshToken.TokenHash,
                UserAgent = _currentUserService.UserAgent,
                IpAddress = _currentUserService.IpAddress,
                ExpiresAt = refreshToken.ExpiresAt
            };
            // STEP-11: Queue session through authenticationRepository.AddSession.
            _authenticationRepository.AddSession(session);
            // STEP-12: When isNewUser is true, call auditLogService.Add.
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

            // STEP-13: Record audit action AuditActions.UserLogin.
            _auditLogService.Add(new AuditLogEntry
            {
                Action = AuditActions.UserLogin,
                EntityType = AuditEntityTypes.User,
                EntityId = user.Id,
                SubjectUserId = user.Id,
                ActorId = user.Id,
                ActorRole = user.Role.Code
            });

            // STEP-14: Save the pending changes.
            await _unitOfWork.SaveChangesAsync(ct);

            // STEP-15: Prepare accessToken through tokenService.CreateAccessToken and tokens as AuthenticationTokens.
            // The session id exists only once saved; the access token carries it as claim sid.
            var accessToken = _tokenService.CreateAccessToken(user, session.Id);
            var tokens = new AuthenticationTokens(
                accessToken.Token,
                accessToken.ExpiresAt,
                refreshToken.Token,
                refreshToken.TokenHash,
                refreshToken.ExpiresAt);
            // STEP-16: Commit the transaction.
            await transaction.CommitAsync(ct);

            // STEP-17: When statusChanged is true, call accountStatusCache.Invalidate.
            if (statusChanged)
            {
                _accountStatusCache.Invalidate(user.Id);
            }

            // STEP-18: Load needsPreferenceSurvey through GetNeedsPreferenceSurveyAsync.
            var needsPreferenceSurvey = await GetNeedsPreferenceSurveyAsync(user, ct);
            // STEP-19: Return the result of AuthenticationResponseMapper.MapGoogle.
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
