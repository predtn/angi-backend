using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the allowed reply states and mandatory reason for MOD-F3.</summary>
public sealed class UpdateReviewReplyStatusRequestDtoValidator : AbstractValidator<UpdateReviewReplyStatusRequestDto>
{
    public UpdateReviewReplyStatusRequestDtoValidator()
    {
        RuleFor(x => x.Status).NotEmpty().WithMessage("Vui lòng chọn trạng thái phản hồi.");
        RuleFor(x => x.Status).Must(x => x is "visible" or "hidden" or "removed")
            .WithMessage("Trạng thái phản hồi không hợp lệ.")
            .When(x => !string.IsNullOrWhiteSpace(x.Status));

        RuleFor(x => x.Reason).NotEmpty().WithMessage("Vui lòng nhập lý do.");
        RuleFor(x => x.Reason).MaximumLength(1000).WithMessage("Lý do không được vượt quá 1000 ký tự.");
    }
}
