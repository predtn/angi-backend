using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the mandatory state and reason for MOD-R3.</summary>
public sealed class UpdateRestaurantModerationStatusRequestDtoValidator : AbstractValidator<UpdateRestaurantModerationStatusRequestDto>
{
    /// <summary>Accepts only documented states and reasons containing one to one thousand characters.</summary>
    public UpdateRestaurantModerationStatusRequestDtoValidator()
    {
        RuleFor(x => x.ModerationStatus).NotEmpty().WithMessage("Vui lòng chọn trạng thái kiểm duyệt.");
        RuleFor(x => x.ModerationStatus).Must(x => x is "visible" or "hidden" or "suspended")
            .WithMessage("Trạng thái kiểm duyệt không hợp lệ.").When(x => !string.IsNullOrWhiteSpace(x.ModerationStatus));
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Vui lòng nhập lý do.");
        RuleFor(x => x.Reason).MaximumLength(1000).WithMessage("Lý do không được vượt quá 1000 ký tự.");
        RuleFor(x => x.ReportId).GreaterThan(0).WithMessage("Mã báo cáo phải lớn hơn 0.").When(x => x.ReportId.HasValue);
    }
}
