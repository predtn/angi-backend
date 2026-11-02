using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the allowed review states and mandatory reason for MOD-F2.</summary>
public sealed class UpdateReviewStatusRequestDtoValidator : AbstractValidator<UpdateReviewStatusRequestDto>
{
    public UpdateReviewStatusRequestDtoValidator()
    {
        RuleFor(x => x.Status).NotEmpty().WithMessage("Vui lòng chọn trạng thái đánh giá.");
        RuleFor(x => x.Status).Must(x => x is "visible" or "hidden" or "removed")
            .WithMessage("Trạng thái đánh giá không hợp lệ.")
            .When(x => !string.IsNullOrWhiteSpace(x.Status));

        RuleFor(x => x.Reason).NotEmpty().WithMessage("Vui lòng nhập lý do.");
        RuleFor(x => x.Reason).MaximumLength(1000).WithMessage("Lý do không được vượt quá 1000 ký tự.");
    }
}
