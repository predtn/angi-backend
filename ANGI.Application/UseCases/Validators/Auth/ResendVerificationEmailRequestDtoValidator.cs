using ANGI.Application.DTOs.Auth;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Auth
{
    public sealed class ResendVerificationEmailRequestDtoValidator : AbstractValidator<ResendVerificationEmailRequestDto>
    {
        /// <summary>Defines the required email format and length rules for AUTH-03.</summary>
        public ResendVerificationEmailRequestDtoValidator()
        {
            RuleFor(x => x.Email)
                .NotEmpty().WithMessage("Email là bắt buộc.")
                .EmailAddress().WithMessage("Email không đúng định dạng.")
                .MaximumLength(255).WithMessage("Email không được vượt quá 255 ký tự.");
        }
    }
}
