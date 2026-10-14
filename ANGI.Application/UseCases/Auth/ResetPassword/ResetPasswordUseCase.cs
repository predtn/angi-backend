using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Auth;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Auth.ResetPassword
{
    /// <summary>Consumes a one-time reset token, replaces its user's password, and revokes all sessions.</summary>
    public sealed class ResetPasswordUseCase : IResetPasswordUseCase
    {
        private const string TokenInvalidCode = "TOKEN_INVALID";
        private const string TokenInvalidMessage = "Token đặt lại mật khẩu không hợp lệ.";
        private const string TokenExpiredCode = "TOKEN_EXPIRED";
        private const string TokenExpiredMessage = "Token đặt lại mật khẩu đã hết hạn.";

        private readonly IValidator<ResetPasswordRequestDto> _validator;
        private readonly IAuthenticationRepository _authenticationRepository;
        private readonly IAuthenticationTokenService _tokenService;
        private readonly IPasswordService _passwordService;
        private readonly IAuditLogService _auditLogService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _timeProvider;

        /// <summary>Initializes the reset workflow with validation, security, persistence, audit, and clock abstractions.</summary>
        public ResetPasswordUseCase(
            IValidator<ResetPasswordRequestDto> validator,
            IAuthenticationRepository authenticationRepository,
            IAuthenticationTokenService tokenService,
            IPasswordService passwordService,
            IAuditLogService auditLogService,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider)
        {
            _validator = validator;
            _authenticationRepository = authenticationRepository;
            _tokenService = tokenService;
            _passwordService = passwordService;
            _auditLogService = auditLogService;
            _unitOfWork = unitOfWork;
            _timeProvider = timeProvider;
        }

        /// <summary>Executes AUTH-09 atomically so a valid token can change the password only once.</summary>
        public async Task ExecuteAsync(ResetPasswordRequestDto request, CancellationToken ct)
        {
            await _validator.ValidateAndThrowAsync(request, ct);

            var tokenHash = _tokenService.HashUserToken(request.Token);
            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            var userToken = await _authenticationRepository.GetUserTokenByHashForUpdateAsync(tokenHash, ct);

            if (userToken is null ||
                userToken.Purpose != UserTokenPurpose.PasswordReset ||
                userToken.UsedAt.HasValue)
            {
                throw new BadRequestException(TokenInvalidCode, TokenInvalidMessage);
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (userToken.ExpiresAt <= now)
            {
                throw new BadRequestException(TokenExpiredCode, TokenExpiredMessage);
            }

            userToken.UsedAt = now;
            userToken.User.PasswordHash = _passwordService.Hash(request.NewPassword);

            var activeSessions = await _authenticationRepository.GetActiveSessionsAsync(userToken.UserId, ct);
            foreach (var session in activeSessions)
            {
                session.RevokedAt = now;
            }

            _auditLogService.Add(new AuditLogEntry
            {
                Action = AuditActions.PasswordReset,
                EntityType = AuditEntityTypes.User,
                EntityId = userToken.UserId,
                SubjectUserId = userToken.UserId,
                ActorId = userToken.UserId,
                ActorRole = userToken.User.Role.Code
            });

            await _unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
    }
}
