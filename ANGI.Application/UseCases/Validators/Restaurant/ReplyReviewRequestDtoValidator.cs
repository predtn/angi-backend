using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates parameters for OWN-20 review reply request.</summary>
public sealed class ReplyReviewRequestDtoValidator : AbstractValidator<ReplyReviewRequestDto>
{
    public ReplyReviewRequestDtoValidator()
    {
        RuleFor(x => x.Content)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithMessage("Nội dung phản hồi không được để trống.")
            .MaximumLength(1000)
            .WithMessage("Nội dung phản hồi không được vượt quá 1000 ký tự.");
    }
}
