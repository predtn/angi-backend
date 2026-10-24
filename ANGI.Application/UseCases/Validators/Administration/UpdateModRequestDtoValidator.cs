using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Administration;

/// <summary>Validates the optional ADM-M4 fields and rejects an empty patch.</summary>
public sealed class UpdateModRequestDtoValidator : AbstractValidator<UpdateModRequestDto>
{
    /// <summary>Creates validation rules for display-name and status replacement.</summary>
    public UpdateModRequestDtoValidator()
    {
        RuleFor(request => request)
            .Must(request => request.DisplayName is not null || request.Status is not null)
            .WithMessage("Phải cung cấp tên hiển thị hoặc trạng thái.");

        RuleFor(request => request.DisplayName)
            .NotEmpty().WithMessage("Tên hiển thị không được để trống.")
            .When(request => request.DisplayName is not null);
        RuleFor(request => request.DisplayName)
            .Must(name => name is not null && ModDisplayNameRules.GetUnicodeLength(name.Trim()) >= 2)
                .WithMessage("Tên hiển thị phải có ít nhất 2 ký tự.")
            .Must(name => name is not null && ModDisplayNameRules.GetUnicodeLength(name.Trim()) <= 100)
                .WithMessage("Tên hiển thị không được vượt quá 100 ký tự.")
            .Must(ModDisplayNameRules.ContainsOnlySafeUnicode)
                .WithMessage("Tên hiển thị chứa ký tự Unicode không được hỗ trợ.")
            .When(request => !string.IsNullOrWhiteSpace(request.DisplayName));

        RuleFor(request => request.Status)
            .Must(status => status is "active" or "deactivated")
            .WithMessage("Trạng thái chỉ có thể là active hoặc deactivated.")
            .When(request => request.Status is not null);
    }
}
