using ANGI.Application.DTOs.Auth;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Auth
{
    public sealed class LogoutRequestDtoValidator : AbstractValidator<LogoutRequestDto>
    {
        /// <summary>Requires AUTH-07 to receive a non-empty refresh token.</summary>
        public LogoutRequestDtoValidator()
        {
            RuleFor(x => x.RefreshToken)
                .NotEmpty().WithMessage("Refresh token là bắt buộc.");
        }
    }
}
