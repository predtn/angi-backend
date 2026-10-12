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
            await _validator.ValidateAndThrowAsync(request, ct);

            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            string recipientEmail;
            string recipientDisplayName;
            string rawToken;

            await using (var transaction = await _unitOfWork.BeginTransactionAsync(ct))
            {
                var user = await _authenticationRepository.GetUserByEmailForUpdateAsync(normalizedEmail, ct);
                if (user is null ||
                    user.Status != UserStatus.PendingVerification ||
                    user.EmailVerifiedAt.HasValue)
                {
                    return;
                }

                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var oldTokens = await _authenticationRepository.GetUnusedUserTokensAsync(
                    user.Id,
                    UserTokenPurpose.EmailVerify,
                    ct);
                foreach (var oldToken in oldTokens)
                {
                    oldToken.UsedAt = now;
                }

                var generatedToken = _tokenService.CreateUserToken();
                _authenticationRepository.AddUserToken(new UserToken
                {
                    UserId = user.Id,
                    Purpose = UserTokenPurpose.EmailVerify,
                    TokenHash = generatedToken.TokenHash,
                    ExpiresAt = now.Add(TokenLifetime)
                });

                await _unitOfWork.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                recipientEmail = user.Email;
                recipientDisplayName = user.DisplayName;
                rawToken = generatedToken.Token;
            }

            await _emailService.SendEmailVerificationAsync(
                recipientEmail,
                recipientDisplayName,
                rawToken,
                TokenLifetime,
                ct);
        }
    }
}
