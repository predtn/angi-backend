using ANGI.Application.DTOs.Auth;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Auth
{
    public sealed class GoogleLoginRequestDtoValidator : AbstractValidator<GoogleLoginRequestDto>
    {
        /// <summary>Defines the token and optional self-registration role rules for AUTH-05.</summary>
        public GoogleLoginRequestDtoValidator()
        {
            RuleFor(request => request.IdToken)
                .NotEmpty().WithMessage("Google ID token là bắt buộc.");

            RuleFor(request => request.Role)
                .Must(role => role is "TRAVELER" or "RESTAURANT_OWNER")
                .When(request => request.Role is not null)
                .WithMessage("Vai trò phải là TRAVELER hoặc RESTAURANT_OWNER.");
        }
    }
}
