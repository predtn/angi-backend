using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates the OWN-05 status value and the optional note.</summary>
public sealed class UpdateOperatingStatusRequestDtoValidator : AbstractValidator<UpdateOperatingStatusRequestDto>
{
    public static readonly IReadOnlySet<string> OperatingStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "open",
        "temporarily_closed",
        "permanently_closed"
    };

    public UpdateOperatingStatusRequestDtoValidator()
    {
        RuleFor(x => x.OperatingStatus)
            .NotEmpty().WithMessage("Trạng thái hoạt động là bắt buộc.");
        RuleFor(x => x.OperatingStatus)
            .Must(status => OperatingStatuses.Contains(status!))
            .WithMessage("Trạng thái hoạt động chỉ có thể là open, temporarily_closed hoặc permanently_closed.")
            .When(x => !string.IsNullOrWhiteSpace(x.OperatingStatus));

        RuleFor(x => x.Note)
            .MaximumLength(500).WithMessage("Ghi chú không được vượt quá 500 ký tự.");
    }
}
