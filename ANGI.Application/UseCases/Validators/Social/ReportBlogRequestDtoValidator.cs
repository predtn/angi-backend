using ANGI.Application.DTOs.Social;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Social;

/// <summary>Validates REP-02 blog reporting.</summary>
public sealed class ReportBlogRequestDtoValidator : AbstractValidator<ReportBlogRequestDto>
{
    public static readonly IReadOnlySet<string> AllowedReasonCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "spam",
        "offensive",
        "wrong_info",
        "scam",
        "other"
    };

    public ReportBlogRequestDtoValidator()
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
