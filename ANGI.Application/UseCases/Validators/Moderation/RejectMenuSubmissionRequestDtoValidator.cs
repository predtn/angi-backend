using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the reject review note for menu moderation.</summary>
public sealed class RejectMenuSubmissionRequestDtoValidator : AbstractValidator<RejectMenuSubmissionRequestDto>
{
    /// <summary>Enforces the documented required note and 1000 character limit.</summary>
    public RejectMenuSubmissionRequestDtoValidator()
    {
        RuleFor(x => x.ReviewNote).NotEmpty().WithMessage("Lý do từ chối không được để trống.");
        RuleFor(x => x.ReviewNote).MaximumLength(1000)
            .When(x => !string.IsNullOrWhiteSpace(x.ReviewNote))
            .WithMessage("Ghi chú không được vượt quá 1000 ký tự.");
    }
}
