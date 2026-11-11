using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates the optional owner note sent with OWN-17.</summary>
public sealed class SubmitMenuSubmissionRequestDtoValidator
    : AbstractValidator<SubmitMenuSubmissionRequestDto>
{
    /// <summary>Creates the documented maximum-length rule for the owner note.</summary>
    public SubmitMenuSubmissionRequestDtoValidator()
    {
        RuleFor(request => request.OwnerNote)
            .MaximumLength(1000)
            .WithMessage("Ghi chú gửi kiểm duyệt không được vượt quá 1000 ký tự.");
    }
}
