using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the approve review note for menu moderation.</summary>
public sealed class ApproveMenuSubmissionRequestDtoValidator : AbstractValidator<ApproveMenuSubmissionRequestDto>
{
    /// <summary>Enforces the documented optional note and 1000 character limit.</summary>
    public ApproveMenuSubmissionRequestDtoValidator()
    {
        RuleFor(x => x.ReviewNote).MaximumLength(1000)
            .WithMessage("Ghi chú không được vượt quá 1000 ký tự.");
    }
}
