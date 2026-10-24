using ANGI.Application.DTOs.Social;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Social;

/// <summary>Validates BLOG-10 comment creation.</summary>
public sealed class CreateBlogCommentRequestDtoValidator : AbstractValidator<CreateBlogCommentRequestDto>
{
    public CreateBlogCommentRequestDtoValidator()
    {
        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Nội dung bình luận là bắt buộc.");

        RuleFor(x => x.Content)
            .MaximumLength(1000).WithMessage("Nội dung bình luận không được vượt quá 1000 ký tự.")
            .When(x => !string.IsNullOrWhiteSpace(x.Content));
    }
}
