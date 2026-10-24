using ANGI.Application.DTOs.Roadmap;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Roadmap;

/// <summary>Validates the RM-13 request body before the item is locked.</summary>
public sealed class RateRoadmapItemRequestDtoValidator : AbstractValidator<RateRoadmapItemRequestDto>
{
    public const string Like = "like";
    public const string Dislike = "dislike";

    /// <summary>Creates the required like / dislike rule for RM-13.</summary>
    public RateRoadmapItemRequestDtoValidator()
    {
        RuleFor(x => x.Value)
            .NotEmpty().WithMessage("value là bắt buộc.");
        RuleFor(x => x.Value)
            .Must(value => value is Like or Dislike)
            .WithMessage("value chỉ có thể là like hoặc dislike.")
            .When(x => !string.IsNullOrEmpty(x.Value));
    }
}
