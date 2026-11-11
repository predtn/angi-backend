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
            // STEP-01: Validate the request.
            await _validator.ValidateAndThrowAsync(request, ct);

            // STEP-02: Prepare tokenHash through tokenService.HashUserToken.
            var tokenHash = _tokenService.HashUserToken(request.Token);
            // STEP-03: Open the database transaction.
            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            // STEP-04: Load userToken through authenticationRepository.GetUserTokenByHashForUpdateAsync.
            var userToken = await _authenticationRepository.GetUserTokenByHashForUpdateAsync(tokenHash, ct);

            // STEP-05: Reject with TOKEN_INVALID when userToken is missing or userToken.Purpose is not UserTokenPurpose.EmailVerify.
            if (userToken is null ||
                userToken.Purpose != UserTokenPurpose.EmailVerify)
            {
                throw new BadRequestException("TOKEN_INVALID", "Token xác minh không hợp lệ.");
            }

            // STEP-06: Reject with TOKEN_ALREADY_USED when userToken.UsedAt has a value.
            if (userToken.UsedAt.HasValue)
            {
                throw new ConflictException("TOKEN_ALREADY_USED", "Token xác minh đã được sử dụng.");
            }

            // STEP-07: Reject with TOKEN_INVALID when userToken.User.Status is not UserStatus.PendingVerification or userToken.User.EmailVerifiedAt has a value.
            if (userToken.User.Status != UserStatus.PendingVerification ||
                userToken.User.EmailVerifiedAt.HasValue)
            {
                throw new BadRequestException("TOKEN_INVALID", "Token xác minh không hợp lệ.");
            }

            // STEP-08: Prepare now from timeProvider.GetUtcNow.
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            // STEP-09: Reject with TOKEN_EXPIRED when userToken.ExpiresAt <= now.
            if (userToken.ExpiresAt <= now)
            {
                throw new BadRequestException("TOKEN_EXPIRED", "Token xác minh đã hết hạn.");
            }

            // STEP-10: Set userToken.UsedAt, userToken.User.EmailVerifiedAt and userToken.User.Status.
            userToken.UsedAt = now;
            userToken.User.EmailVerifiedAt = now;
            userToken.User.Status = UserStatus.Active;

            // STEP-11: Save the pending changes.
            await _unitOfWork.SaveChangesAsync(ct);
            // STEP-12: Commit the transaction.
            await transaction.CommitAsync(ct);
            // STEP-13: Call accountStatusCache.Invalidate.
            _accountStatusCache.Invalidate(userToken.UserId);

            // STEP-14: Return a new VerifyEmailResponseDto.
            return new VerifyEmailResponseDto
            {
                UserId = userToken.UserId,
                EmailVerifiedAt = now,
                Status = "active"
            };
        }
    }
}
