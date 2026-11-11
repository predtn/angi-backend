using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the allowed dish states and mandatory explanation for MOD-R4.</summary>
public sealed class UpdateDishModerationStatusRequestDtoValidator : AbstractValidator<UpdateDishModerationStatusRequestDto>
{
    /// <summary>Excludes removed states and validates required text without duplicate null errors.</summary>
    public UpdateDishModerationStatusRequestDtoValidator()
    {
        RuleFor(x => x.Status).NotEmpty().WithMessage("Vui lòng chọn trạng thái món ăn.");
        RuleFor(x => x.Status).Must(x => x is "active" or "hidden").WithMessage("Trạng thái món ăn không hợp lệ.")
            .When(x => !string.IsNullOrWhiteSpace(x.Status));
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Vui lòng nhập lý do.");
        RuleFor(x => x.Reason).MaximumLength(1000).WithMessage("Lý do không được vượt quá 1000 ký tự.");
    }
}
