using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Auth.VerifyEmail
{
    /// <summary>Validates and consumes an email-verification token, then activates the account.</summary>
    public sealed class VerifyEmailUseCase : IVerifyEmailUseCase
    {
        private readonly IValidator<VerifyEmailRequestDto> _validator;
        private readonly IAuthenticationRepository _authenticationRepository;
        private readonly IAuthenticationTokenService _tokenService;
        private readonly IAccountStatusCache _accountStatusCache;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _timeProvider;

        /// <summary>Initializes the use case with validation, token, persistence, and clock abstractions.</summary>
        public VerifyEmailUseCase(
            IValidator<VerifyEmailRequestDto> validator,
            IAuthenticationRepository authenticationRepository,
            IAuthenticationTokenService tokenService,
            IAccountStatusCache accountStatusCache,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider)
        {
            _validator = validator;
            _authenticationRepository = authenticationRepository;
            _tokenService = tokenService;
            _accountStatusCache = accountStatusCache;
            _unitOfWork = unitOfWork;
            _timeProvider = timeProvider;
        }

        /// <summary>Consumes a valid AUTH-02 token exactly once and returns the activated account state.</summary>
        public async Task<VerifyEmailResponseDto> ExecuteAsync(
            VerifyEmailRequestDto request,
            CancellationToken ct)
        {
            await _validator.ValidateAndThrowAsync(request, ct);

            var tokenHash = _tokenService.HashUserToken(request.Token);
            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            var userToken = await _authenticationRepository.GetUserTokenByHashForUpdateAsync(tokenHash, ct);

            if (userToken is null ||
                userToken.Purpose != UserTokenPurpose.EmailVerify)
            {
                throw new BadRequestException("TOKEN_INVALID", "Token xác minh không hợp lệ.");
            }

            if (userToken.UsedAt.HasValue)
            {
                throw new ConflictException("TOKEN_ALREADY_USED", "Token xác minh đã được sử dụng.");
            }

            if (userToken.User.Status != UserStatus.PendingVerification ||
                userToken.User.EmailVerifiedAt.HasValue)
            {
                throw new BadRequestException("TOKEN_INVALID", "Token xác minh không hợp lệ.");
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (userToken.ExpiresAt <= now)
            {
                throw new BadRequestException("TOKEN_EXPIRED", "Token xác minh đã hết hạn.");
            }

            userToken.UsedAt = now;
            userToken.User.EmailVerifiedAt = now;
            userToken.User.Status = UserStatus.Active;

            await _unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            _accountStatusCache.Invalidate(userToken.UserId);

            return new VerifyEmailResponseDto
            {
                UserId = userToken.UserId,
                EmailVerifiedAt = now,
                Status = "active"
            };
        }
    }
}
