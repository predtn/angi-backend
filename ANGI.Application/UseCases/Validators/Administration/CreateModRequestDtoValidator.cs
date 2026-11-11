using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Administration;

/// <summary>Validates every ADM-M2 account and optional permission-override field.</summary>
public sealed class CreateModRequestDtoValidator : AbstractValidator<CreateModRequestDto>
{
    /// <summary>Creates validation rules matching the MOD invitation contract.</summary>
    public CreateModRequestDtoValidator()
    {
        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("Email là bắt buộc.")
            .EmailAddress().WithMessage("Email không đúng định dạng.")
            .MaximumLength(255).WithMessage("Email không được vượt quá 255 ký tự.");

        RuleFor(request => request.DisplayName)
            .NotEmpty().WithMessage("Tên hiển thị là bắt buộc.");
        RuleFor(request => request.DisplayName)
            .Must(name => name is not null && ModDisplayNameRules.GetUnicodeLength(name.Trim()) >= 2)
                .WithMessage("Tên hiển thị phải có ít nhất 2 ký tự.")
            .Must(name => name is not null && ModDisplayNameRules.GetUnicodeLength(name.Trim()) <= 100)
                .WithMessage("Tên hiển thị không được vượt quá 100 ký tự.")
            .Must(ModDisplayNameRules.ContainsOnlySafeUnicode)
                .WithMessage("Tên hiển thị chứa ký tự Unicode không được hỗ trợ.")
            .When(request => !string.IsNullOrWhiteSpace(request.DisplayName));

        RuleForEach(request => request.PermissionOverrides)
            .NotNull().WithMessage("Ghi đè quyền không được là null.")
            .SetValidator(new PermissionOverrideDtoValidator())
            .When(request => request.PermissionOverrides is not null);
        RuleFor(request => request.PermissionOverrides)
            .Must(overrides => overrides is null || overrides
                .Where(item => item is not null)
                .Select(item => item.PermissionCode)
                .Distinct(StringComparer.Ordinal)
                .Count() == overrides.Count(item => item is not null))
            .WithMessage("Mỗi mã quyền chỉ được xuất hiện một lần.");
    }
}
