using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates REP-01 restaurant reporting.</summary>
public sealed class CreateRestaurantReportRequestDtoValidator : AbstractValidator<CreateRestaurantReportRequestDto>
{
    public static readonly IReadOnlySet<string> AllowedReasonCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "wrong_info",
        "closed",
        "spam",
        "offensive",
        "hygiene",
        "scam",
        "other"
    };

    public CreateRestaurantReportRequestDtoValidator()
    {
        RuleFor(x => x.ReasonCode)
            .NotEmpty().WithMessage("Lý do báo cáo là bắt buộc.");

        RuleFor(x => x.ReasonCode)
            .Must(code => AllowedReasonCodes.Contains(code!))
            .WithMessage("Lý do báo cáo không hợp lệ.")
            .When(x => !string.IsNullOrWhiteSpace(x.ReasonCode));

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Mô tả chi tiết là bắt buộc khi chọn lý do khác.")
            .When(x => string.Equals(x.ReasonCode, "other", StringComparison.OrdinalIgnoreCase));

        RuleFor(x => x.Description)
            .MaximumLength(1000).WithMessage("Mô tả không được vượt quá 1000 ký tự.")
            .When(x => !string.IsNullOrWhiteSpace(x.Description));

        RuleFor(x => x.EvidenceMediaIds)
            .Must(ids => ids == null || ids.Count <= 5)
            .WithMessage("Số lượng bằng chứng không được vượt quá 5.");
    }
}
