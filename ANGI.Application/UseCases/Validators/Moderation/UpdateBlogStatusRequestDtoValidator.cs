using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the status and reason for MOD-B2.</summary>
public sealed class UpdateBlogStatusRequestDtoValidator : AbstractValidator<UpdateBlogStatusRequestDto>
{
    /// <summary>Ensures the blog status is valid and reason meets length requirements.</summary>
    public UpdateBlogStatusRequestDtoValidator()
    {
        RuleFor(x => x.Status).NotEmpty().WithMessage("Vui lòng chọn trạng thái bài viết.");
        RuleFor(x => x.Status)
            .Must(x => x is "published" or "hidden" or "removed")
            .WithMessage("Trạng thái bài viết không hợp lệ.")
            .When(x => !string.IsNullOrWhiteSpace(x.Status));

        RuleFor(x => x.Reason).NotEmpty().WithMessage("Vui lòng nhập lý do.");
        RuleFor(x => x.Reason)
            .MaximumLength(1000)
            .WithMessage("Lý do không được vượt quá 1000 ký tự.")
            .When(x => !string.IsNullOrWhiteSpace(x.Reason));
    }
}
