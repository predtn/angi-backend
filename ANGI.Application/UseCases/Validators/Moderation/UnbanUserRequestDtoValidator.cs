using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the mandatory reason for MOD-U4.</summary>
public sealed class UnbanUserRequestDtoValidator : AbstractValidator<UnbanUserRequestDto>
{
    public UnbanUserRequestDtoValidator()
    {
        RuleFor(x => x.LiftReason)
            .NotEmpty().WithMessage("Vui lòng nhập lý do gỡ cấm.");

        RuleFor(x => x.LiftReason)
            .MaximumLength(1000).WithMessage("Lý do không được vượt quá 1000 ký tự.")
            .When(x => !string.IsNullOrWhiteSpace(x.LiftReason));
    }
}
