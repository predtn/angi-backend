using ANGI.Application.DTOs.Social;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Social;

/// <summary>Validates partial update fields for BLOG-06.</summary>
public sealed class EditBlogRequestDtoValidator : AbstractValidator<EditBlogRequestDto>
{
    public EditBlogRequestDtoValidator()
    {
        When(request => request.TitleSpecified, () =>
        {
            RuleFor(request => request.Title)
                .NotEmpty().WithMessage("Tiêu đề bài viết không được để trống.")
                .MaximumLength(255).WithMessage("Tiêu đề bài viết không được vượt quá 255 ký tự.");
        });

        When(request => request.ContentSpecified, () =>
        {
            RuleFor(request => request.Content)
                .NotEmpty().WithMessage("Nội dung bài viết không được để trống.");
        });

        When(request => request.CoverMediaIdSpecified && request.CoverMediaId.HasValue, () =>
        {
            RuleFor(request => request.CoverMediaId)
                .GreaterThan(0).WithMessage("Mã ảnh bìa phải lớn hơn 0.");
        });

        When(request => request.ImageMediaIdsSpecified && request.ImageMediaIds is not null, () =>
        {
            RuleFor(request => request.ImageMediaIds)
                .Must(ids => ids!.Count <= 20).WithMessage("Chỉ được chọn tối đa 20 ảnh.")
                .Must(ids => ids!.All(id => id > 0)).WithMessage("Mã ảnh phải lớn hơn 0.")
                .Must(ids => ids!.Distinct().Count() == ids!.Count).WithMessage("Danh sách ảnh không được trùng nhau.");
        });
    }
}
