using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Administration;

/// <summary>Validates the complete ADM-P4 MOD role-default replacement payload.</summary>
public sealed class ReplaceModRolePermissionsRequestDtoValidator : AbstractValidator<ReplaceModRolePermissionsRequestDto>
{
    /// <summary>Defines required-array, non-empty-code, and duplicate-code rules.</summary>
    public ReplaceModRolePermissionsRequestDtoValidator()
    {
        RuleFor(x => x.PermissionCodes)
            .NotNull()
            .WithMessage("Danh sách mã quyền là bắt buộc.");

        RuleForEach(x => x.PermissionCodes)
            .NotEmpty()
            .WithMessage("Mã quyền không được để trống.");

        RuleFor(x => x.PermissionCodes)
            .Must(codes => codes is null || codes.Distinct(StringComparer.Ordinal).Count() == codes.Count)
            .WithMessage("Mỗi quyền chỉ được khai báo một lần.");
    }
}
