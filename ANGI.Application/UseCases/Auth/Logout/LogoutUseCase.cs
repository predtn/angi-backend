using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.DTOs.Auth;
using FluentValidation;

namespace ANGI.Application.UseCases.Auth.Logout
{
    /// <summary>Coordinates logout for a session owned by the current user.</summary>
    public sealed class LogoutUseCase : ILogoutUseCase
    {
        private readonly IValidator<LogoutRequestDto> _validator;
        private readonly IAuthenticationRepository _authenticationRepository;
        private readonly IAuthenticationTokenService _tokenService;
        private readonly ICurrentUserService _currentUserService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _timeProvider;

        /// <summary>Initializes the use case with validator, repository, current-user, and Unit of Work abstractions.</summary>
        public LogoutUseCase(
            IValidator<LogoutRequestDto> validator,
            IAuthenticationRepository authenticationRepository,
            IAuthenticationTokenService tokenService,
            ICurrentUserService currentUserService,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider)
        {
            _validator = validator;
            _authenticationRepository = authenticationRepository;
            _tokenService = tokenService;
            _currentUserService = currentUserService;
            _unitOfWork = unitOfWork;
            _timeProvider = timeProvider;
        }

        /// <summary>
        /// Revokes the session matching the refresh token and current user while remaining idempotent for invalid or revoked tokens.
        /// </summary>
        public async Task ExecuteAsync(LogoutRequestDto request, CancellationToken ct)
        {
            await _validator.ValidateAndThrowAsync(request, ct);

            var refreshTokenHash = _tokenService.HashRefreshToken(request.RefreshToken);
            var session = await _authenticationRepository.GetSessionByRefreshTokenHashAsync(refreshTokenHash, ct);

            if (session is null ||
                session.RevokedAt.HasValue ||
                session.UserId != _currentUserService.UserId)
            {
                return;
            }

            session.RevokedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await _unitOfWork.SaveChangesAsync(ct);
        }
    }
}
