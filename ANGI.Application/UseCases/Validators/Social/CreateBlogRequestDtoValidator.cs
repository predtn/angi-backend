using ANGI.Application.DTOs.Social;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Social;

/// <summary>Validates the BLOG-04 request payload before use case execution.</summary>
public sealed class CreateBlogRequestDtoValidator : AbstractValidator<CreateBlogRequestDto>
{
    public CreateBlogRequestDtoValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Tiêu đề bài viết là bắt buộc.");
        RuleFor(x => x.Title)
            .MaximumLength(255).WithMessage("Tiêu đề bài viết không được vượt quá 255 ký tự.")
            .When(x => !string.IsNullOrWhiteSpace(x.Title));

        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Nội dung bài viết là bắt buộc.");

        RuleFor(x => x.CoverMediaId)
            .GreaterThan(0).WithMessage("ID ảnh bìa không hợp lệ.")
            .When(x => x.CoverMediaId.HasValue);

        RuleFor(x => x.ImageMediaIds)
            .Must(ids => ids == null || ids.Count <= 20).WithMessage("Số lượng ảnh đính kèm không được vượt quá 20 ảnh.");

        RuleForEach(x => x.ImageMediaIds)
            .GreaterThan(0).WithMessage("ID ảnh đính kèm không hợp lệ.")
            .When(x => x.ImageMediaIds != null);

        RuleFor(x => x.RoadmapId)
            .GreaterThan(0).WithMessage("ID lộ trình không hợp lệ.")
            .When(x => x.RoadmapId.HasValue);
    }
}
