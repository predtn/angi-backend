using ANGI.Application.DTOs.Account;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Account;

/// <summary>Validates the ME-03 change password request according to security and business rules.</summary>
public sealed class ChangePasswordRequestDtoValidator : AbstractValidator<ChangePasswordRequestDto>
{
    public ChangePasswordRequestDtoValidator()
    {
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("Mật khẩu mới là bắt buộc.");
        // Format rules only for a value that was sent, so a missing one gets the single message above.
        RuleFor(x => x.NewPassword)
            .MinimumLength(8).WithMessage("Mật khẩu mới phải có ít nhất 8 ký tự.")
            .MaximumLength(64).WithMessage("Mật khẩu mới không được vượt quá 64 ký tự.")
            .Must(password => password is not null && password.Any(char.IsLetter))
                .WithMessage("Mật khẩu mới phải có ít nhất một chữ cái.")
            .Must(password => password is not null && password.Any(char.IsDigit))
                .WithMessage("Mật khẩu mới phải có ít nhất một chữ số.")
            .Must((dto, newPassword) =>
            {
                var current = dto.CurrentPassword ?? dto.OldPassword;
                return current is null || !string.Equals(current, newPassword, StringComparison.Ordinal);
            })
            .WithMessage("Mật khẩu mới không được trùng với mật khẩu cũ.")
            .When(x => !string.IsNullOrWhiteSpace(x.NewPassword));
    }
}
