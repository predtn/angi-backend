using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Administration;

/// <summary>Validates the complete ADM-P3 MOD override replacement payload.</summary>
public sealed class ReplaceModPermissionsRequestDtoValidator : AbstractValidator<ReplaceModPermissionsRequestDto>
{
    /// <summary>Defines required-array, duplicate-code, and nested override validation rules.</summary>
    public ReplaceModPermissionsRequestDtoValidator()
    {
        RuleFor(x => x.Overrides)
            .NotNull()
            .WithMessage("Danh sách ghi đè quyền là bắt buộc.");

        RuleFor(x => x.Overrides)
            .Must(items => items is null || items
                .Select(item => item.PermissionCode)
                .Distinct(StringComparer.Ordinal)
                .Count() == items.Count)
            .WithMessage("Mỗi quyền chỉ được ghi đè một lần.");

        RuleForEach(x => x.Overrides)
            .SetValidator(new PermissionOverrideDtoValidator());
    }
}

/// <summary>Validates one permission override nested inside ADM-P3.</summary>
public sealed class PermissionOverrideDtoValidator : AbstractValidator<PermissionOverrideDto>
{
    private static readonly string[] Effects = ["allow", "deny"];

    /// <summary>Defines required permission-code and exact enum-value rules.</summary>
    public PermissionOverrideDtoValidator()
    {
        RuleFor(x => x.PermissionCode)
            .NotEmpty()
            .WithMessage("Mã quyền là bắt buộc.");

        RuleFor(x => x.Effect)
            .NotEmpty()
            .WithMessage("Hiệu lực ghi đè là bắt buộc.");

        RuleFor(x => x.Effect)
            .Must(effect => Effects.Contains(effect, StringComparer.Ordinal))
            .When(x => !string.IsNullOrWhiteSpace(x.Effect))
            .WithMessage("Hiệu lực ghi đè chỉ nhận allow hoặc deny.");
    }
}
