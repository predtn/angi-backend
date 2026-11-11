using ANGI.Application.DTOs.Social;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Social;

/// <summary>Validates every field and collection boundary documented for BLOG-04.</summary>
public sealed class CreateBlogRequestDtoValidator : AbstractValidator<CreateBlogRequestDto>
{
    /// <summary>Creates required text, identifier, uniqueness, and 20-image limit rules.</summary>
    public CreateBlogRequestDtoValidator()
    {
        RuleFor(request => request.Title)
            .NotEmpty().WithMessage("Tiêu đề bài viết là bắt buộc.")
            .MaximumLength(255).WithMessage("Tiêu đề bài viết không được vượt quá 255 ký tự.");
        RuleFor(request => request.Content)
            .NotEmpty().WithMessage("Nội dung bài viết là bắt buộc.");
        RuleFor(request => request.CoverMediaId)
            .GreaterThan(0).WithMessage("Mã ảnh bìa phải lớn hơn 0.")
            .When(request => request.CoverMediaId.HasValue);
        RuleFor(request => request.RoadmapId)
            .GreaterThan(0).WithMessage("Mã roadmap phải lớn hơn 0.")
            .When(request => request.RoadmapId.HasValue);
        RuleFor(request => request.ImageMediaIds)
            .Must(ids => ids is null || ids.Count <= 20)
            .WithMessage("Chỉ được chọn tối đa 20 ảnh.")
            .Must(ids => ids is null || ids.All(id => id > 0))
            .WithMessage("Mã ảnh phải lớn hơn 0.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Danh sách ảnh không được trùng nhau.");
    }
}
