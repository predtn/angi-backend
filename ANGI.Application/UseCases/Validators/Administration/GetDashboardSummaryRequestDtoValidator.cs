using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Administration;

/// <summary>Validates ADM-D1 reporting range parameters.</summary>
public sealed class GetDashboardSummaryRequestDtoValidator : AbstractValidator<GetDashboardSummaryRequestDto>
{
    /// <summary>Creates validation rules for optional date boundaries and range limits.</summary>
    public GetDashboardSummaryRequestDtoValidator()
    {
        RuleFor(request => request)
            .Must(request => !(request.From.HasValue && request.To.HasValue) || request.From.Value <= request.To.Value)
            .WithMessage("startDate phải nhỏ hơn hoặc bằng endDate.")
            .Must(request => !(request.From.HasValue && request.To.HasValue) || (request.To.Value.DayNumber - request.From.Value.DayNumber) <= 366)
            .WithMessage("Khoảng thời gian không được vượt quá 366 ngày.");
    }
}
