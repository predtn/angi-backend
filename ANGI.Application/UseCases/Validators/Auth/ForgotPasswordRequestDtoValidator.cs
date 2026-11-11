using ANGI.Application.DTOs.Auth;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Auth
{
    /// <summary>Validates the email supplied to AUTH-08 before any account lookup occurs.</summary>
    public sealed class ForgotPasswordRequestDtoValidator : AbstractValidator<ForgotPasswordRequestDto>
    {
        /// <summary>Defines the required email format and database-length boundary for AUTH-08.</summary>
        public ForgotPasswordRequestDtoValidator()
        {
            RuleFor(x => x.Email)
                .NotEmpty().WithMessage("Email là bắt buộc.")
                .EmailAddress().WithMessage("Email không đúng định dạng.")
                .MaximumLength(255).WithMessage("Email không được vượt quá 255 ký tự.");
        }
    }
}
