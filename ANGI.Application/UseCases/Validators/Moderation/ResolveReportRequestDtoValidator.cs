using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the MOD-RP4 conclusion, action and conditional suspension deadline.</summary>
public sealed class ResolveReportRequestDtoValidator : AbstractValidator<ResolveReportRequestDto>
{
    /// <summary>Requires documented enum strings and a future deadline when suspending an account.</summary>
    public ResolveReportRequestDtoValidator(TimeProvider clock)
    {
        RuleFor(x => x.Outcome).NotEmpty().WithMessage("Vui lòng chọn kết luận báo cáo.");
        RuleFor(x => x.Outcome).Must(x => x is "resolved" or "dismissed").WithMessage("Kết luận không hợp lệ.")
            .When(x => !string.IsNullOrWhiteSpace(x.Outcome));
        RuleFor(x => x.ResolutionAction).NotEmpty().WithMessage("Vui lòng chọn hành động xử lý.");
        RuleFor(x => x.ResolutionAction).Must(x => x is "none" or "hide_blog" or "remove_blog" or "warn_user" or
                "suspend_user" or "ban_user" or "hide_restaurant" or "suspend_restaurant")
            .WithMessage("Hành động xử lý không hợp lệ.").When(x => !string.IsNullOrWhiteSpace(x.ResolutionAction));
        RuleFor(x => x.ResolutionNote).MaximumLength(1000).WithMessage("Ghi chú không được vượt quá 1000 ký tự.");
        RuleFor(x => x.SuspendUntil).NotNull().WithMessage("Vui lòng nhập thời điểm hết hạn tạm khóa.")
            .When(x => x.ResolutionAction == "suspend_user");
        RuleFor(x => x.SuspendUntil).Must(value => value > clock.GetUtcNow())
            .WithMessage("Thời điểm hết hạn tạm khóa phải ở tương lai.")
            .When(x => x.ResolutionAction == "suspend_user" && x.SuspendUntil.HasValue);
    }
}
