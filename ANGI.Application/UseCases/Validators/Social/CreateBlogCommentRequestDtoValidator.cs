using ANGI.Application.DTOs.Social;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Social;

/// <summary>Validates request boundaries documented for BLOG-10.</summary>
public sealed class CreateBlogCommentRequestDtoValidator : AbstractValidator<CreateBlogCommentRequestDto>
{
    /// <summary>Enforces non-empty and 1000-character maximum limits on comment content.</summary>
    public CreateBlogCommentRequestDtoValidator()
    {
        RuleFor(request => request.Content)
            .NotEmpty()
            .WithMessage("Nội dung bình luận là bắt buộc.");

        RuleFor(request => request.Content)
            .MaximumLength(1000)
            .WithMessage("Nội dung bình luận không được vượt quá 1000 ký tự.")
            .When(request => !string.IsNullOrWhiteSpace(request.Content));
    }
}
