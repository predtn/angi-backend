using ANGI.Application.DTOs.Auth;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Auth
{
    public sealed class RefreshTokenRequestDtoValidator : AbstractValidator<RefreshTokenRequestDto>
    {
        /// <summary>Requires AUTH-06 to receive a non-empty refresh token.</summary>
        public RefreshTokenRequestDtoValidator()
        {
            RuleFor(x => x.RefreshToken)
                .NotEmpty().WithMessage("Refresh token là bắt buộc.");
        }
    }
}
