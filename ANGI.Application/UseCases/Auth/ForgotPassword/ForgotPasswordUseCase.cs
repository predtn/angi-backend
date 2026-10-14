using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Auth.ForgotPassword
{
    /// <summary>Creates and emails a one-time password-reset token without exposing account existence.</summary>
    public sealed class ForgotPasswordUseCase : IForgotPasswordUseCase
    {
        private static readonly TimeSpan _tokenLifetime = TimeSpan.FromMinutes(30);

        private readonly IValidator<ForgotPasswordRequestDto> _validator;
        private readonly IAuthenticationRepository _authenticationRepository;
        private readonly IAuthenticationTokenService _tokenService;
        private readonly IEmailService _emailService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _timeProvider;

        /// <summary>Initializes the password-reset request workflow and its required abstractions.</summary>
        public ForgotPasswordUseCase(
            IValidator<ForgotPasswordRequestDto> validator,
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

        /// <summary>Handles AUTH-08 by atomically replacing reset tokens and sending a 30-minute link.</summary>
        public async Task ExecuteAsync(ForgotPasswordRequestDto request, CancellationToken ct)
        {
            await _validator.ValidateAndThrowAsync(request, ct);

            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            string recipientEmail;
            string recipientDisplayName;
            string rawToken;

            await using (var transaction = await _unitOfWork.BeginTransactionAsync(ct))
            {
                var user = await _authenticationRepository.GetUserByEmailForUpdateAsync(normalizedEmail, ct);
                if (user is null)
                {
                    return;
                }

                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var oldTokens = await _authenticationRepository.GetUnusedUserTokensAsync(
                    user.Id,
                    UserTokenPurpose.PasswordReset,
                    ct);
                foreach (var oldToken in oldTokens)
                {
                    oldToken.UsedAt = now;
                }

                var generatedToken = _tokenService.CreateUserToken();
                _authenticationRepository.AddUserToken(new UserToken
                {
                    UserId = user.Id,
                    Purpose = UserTokenPurpose.PasswordReset,
                    TokenHash = generatedToken.TokenHash,
                    ExpiresAt = now.Add(_tokenLifetime)
                });

                await _unitOfWork.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                recipientEmail = user.Email;
                recipientDisplayName = user.DisplayName;
                rawToken = generatedToken.Token;
            }

            await _emailService.SendPasswordResetAsync(
                recipientEmail,
                recipientDisplayName,
                rawToken,
                _tokenLifetime,
                ct);
        }
    }
}
