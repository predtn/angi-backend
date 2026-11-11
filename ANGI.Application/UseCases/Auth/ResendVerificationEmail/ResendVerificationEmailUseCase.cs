using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Auth.ResendVerificationEmail
{
    /// <summary>Replaces outstanding email-verification tokens and sends a fresh verification link.</summary>
    public sealed class ResendVerificationEmailUseCase : IResendVerificationEmailUseCase
    {
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

        private readonly IValidator<ResendVerificationEmailRequestDto> _validator;
        private readonly IAuthenticationRepository _authenticationRepository;
        private readonly IAuthenticationTokenService _tokenService;
        private readonly IEmailService _emailService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _timeProvider;

        /// <summary>Initializes the use case with validation, persistence, token, email, and clock abstractions.</summary>
        public ResendVerificationEmailUseCase(
            IValidator<ResendVerificationEmailRequestDto> validator,
            IAuthenticationRepository authenticationRepository,
            IAuthenticationTokenService tokenService,
            IEmailService emailService,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider)
        {
            _validator = validator;
            _authenticationRepository = authenticationRepository;
            _tokenService = tokenService;
            _emailService = emailService;
            _unitOfWork = unitOfWork;
            _timeProvider = timeProvider;
        }

        /// <summary>
        /// Handles AUTH-03 without revealing account existence and sends a new token only to unverified users.
        /// </summary>
        public async Task ExecuteAsync(ResendVerificationEmailRequestDto request, CancellationToken ct)
        {
            // STEP-01: Validate the request.
            await _validator.ValidateAndThrowAsync(request, ct);

            // STEP-02: Prepare normalizedEmail through request.Email.Trim().ToLowerInvariant, recipientEmail, recipientDisplayName and rawToken.
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            string recipientEmail;
            string recipientDisplayName;
            string rawToken;

            // STEP-03: Hold the scoped resource for the block below.
            await using (var transaction = await _unitOfWork.BeginTransactionAsync(ct))
            {
                // STEP-04: Load user through authenticationRepository.GetUserByEmailForUpdateAsync.
                var user = await _authenticationRepository.GetUserByEmailForUpdateAsync(normalizedEmail, ct);
                // STEP-05: Stop here when user is missing, user.Status is not UserStatus.PendingVerification or user.EmailVerifiedAt has a value.
                if (user is null ||
                    user.Status != UserStatus.PendingVerification ||
                    user.EmailVerifiedAt.HasValue)
                {
                    return;
                }

                // STEP-06: Prepare now, then load oldTokens through authenticationRepository.GetUnusedUserTokensAsync.
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var oldTokens = await _authenticationRepository.GetUnusedUserTokensAsync(
                    user.Id,
                    UserTokenPurpose.EmailVerify,
                    ct);
                // STEP-07: For each oldToken in oldTokens, set oldToken.UsedAt.
                foreach (var oldToken in oldTokens)
                {
                    oldToken.UsedAt = now;
                }

                // STEP-08: Prepare generatedToken through tokenService.CreateUserToken.
                var generatedToken = _tokenService.CreateUserToken();
                // STEP-09: Queue a new UserToken through authenticationRepository.AddUserToken.
                _authenticationRepository.AddUserToken(new UserToken
                {
                    UserId = user.Id,
                    Purpose = UserTokenPurpose.EmailVerify,
                    TokenHash = generatedToken.TokenHash,
                    ExpiresAt = now.Add(TokenLifetime)
                });

                // STEP-10: Save the pending changes.
                await _unitOfWork.SaveChangesAsync(ct);
                // STEP-11: Commit the transaction.
                await transaction.CommitAsync(ct);
                // STEP-12: Set recipientEmail, recipientDisplayName and rawToken.
                recipientEmail = user.Email;
                recipientDisplayName = user.DisplayName;
                rawToken = generatedToken.Token;
            }

            // STEP-13: Call emailService.SendEmailVerificationAsync.
            await _emailService.SendEmailVerificationAsync(
                recipientEmail,
                recipientDisplayName,
                rawToken,
                TokenLifetime,
                ct);
        }
    }
}
