using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the MOD-V3 decision and conditional review note.</summary>
public sealed class ReviewVerificationRequestDtoValidator : AbstractValidator<ReviewVerificationRequestDto>
{
    private static readonly string[] AllowedDecisions = ["approve", "reject", "needs_more_info"];

    /// <summary>Requires a documented decision and a note for rejection or additional-information requests.</summary>
    public ReviewVerificationRequestDtoValidator()
    {
        RuleFor(request => request.Decision)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("decision không được để trống.")
            .Must(decision => AllowedDecisions.Contains(decision, StringComparer.OrdinalIgnoreCase))
            .WithMessage("decision phải là approve, reject hoặc needs_more_info.");

        RuleFor(request => request.ReviewNote)
            .MaximumLength(1000)
            .WithMessage("reviewNote không được vượt quá 1000 ký tự.");

        RuleFor(request => request.ReviewNote)
            .NotEmpty()
            .When(request => string.Equals(request.Decision, "reject", StringComparison.OrdinalIgnoreCase)
                || string.Equals(request.Decision, "needs_more_info", StringComparison.OrdinalIgnoreCase))
            .WithMessage("reviewNote là bắt buộc khi từ chối hoặc yêu cầu bổ sung.");
    }
}
