using ANGI.Application.DTOs.Auth;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Auth
{
    /// <summary>Validates AUTH-09 reset-token and replacement-password fields.</summary>
    public sealed class ResetPasswordRequestDtoValidator : AbstractValidator<ResetPasswordRequestDto>
    {
        /// <summary>Defines the required token and documented 8-to-64-character password rules.</summary>
        public ResetPasswordRequestDtoValidator()
        {
            RuleFor(x => x.Token)
                .NotEmpty().WithMessage("Token đặt lại mật khẩu là bắt buộc.");

            RuleFor(x => x.NewPassword)
                .NotEmpty().WithMessage("Mật khẩu mới là bắt buộc.")
                .MinimumLength(8).WithMessage("Mật khẩu mới phải có ít nhất 8 ký tự.")
                .MaximumLength(64).WithMessage("Mật khẩu mới không được vượt quá 64 ký tự.")
                .Must(password => password is not null && password.Any(char.IsLetter))
                    .WithMessage("Mật khẩu mới phải có ít nhất một chữ cái.")
                .Must(password => password is not null && password.Any(char.IsDigit))
                    .WithMessage("Mật khẩu mới phải có ít nhất một chữ số.");
        }
    }
}
