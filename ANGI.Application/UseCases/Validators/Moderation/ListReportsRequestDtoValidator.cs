using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the exact MOD-RP1 enums, positive target identifier and bounded page size.</summary>
public sealed class ListReportsRequestDtoValidator : AbstractValidator<ListReportsRequestDto>
{
    /// <summary>Defines required target selection and optional filter and pagination boundaries.</summary>
    public ListReportsRequestDtoValidator()
    {
        RuleFor(x => x.TargetType).NotEmpty().WithMessage("Loại đối tượng là bắt buộc.");
        RuleFor(x => x.TargetType).Must(x => x is "restaurant" or "blog")
            .When(x => !string.IsNullOrWhiteSpace(x.TargetType)).WithMessage("Loại đối tượng không hợp lệ.");
        RuleFor(x => x.Status).Must(x => x is null or "pending" or "in_review" or "resolved" or "dismissed")
            .WithMessage("Trạng thái báo cáo không hợp lệ.");
        RuleFor(x => x.ReasonCode).Must(x => x is null or "wrong_info" or "closed" or "spam" or "offensive" or "hygiene" or "scam" or "other")
            .WithMessage("Lý do báo cáo không hợp lệ.");
        RuleFor(x => x.TargetId).GreaterThan(0).When(x => x.TargetId.HasValue).WithMessage("Mã đối tượng phải lớn hơn 0.");
        RuleFor(x => x.Page).GreaterThan(0).WithMessage("Trang phải lớn hơn 0.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Kích thước trang phải từ 1 đến 100.");
    }
}
