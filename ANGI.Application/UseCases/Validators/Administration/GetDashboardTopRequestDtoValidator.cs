using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Administration;

/// <summary>Validates ADM-D3 entity category and result count parameters.</summary>
public sealed class GetDashboardTopRequestDtoValidator : AbstractValidator<GetDashboardTopRequestDto>
{
    private static readonly string[] AllowedTypes = ["dishes", "restaurants", "blogs"];

    /// <summary>Creates validation rules for category and limit.</summary>
    public GetDashboardTopRequestDtoValidator()
    {
        RuleFor(request => request.Type)
            .NotEmpty().WithMessage("type không được để trống.")
            .Must(type => string.IsNullOrWhiteSpace(type) || AllowedTypes.Contains(type.Trim().ToLowerInvariant()))
            .WithMessage("type không hợp lệ. Cho phép: dishes, restaurants, blogs.");

        RuleFor(request => request.Limit)
            .InclusiveBetween(1, 50)
            .When(request => request.Limit.HasValue)
            .WithMessage("limit phải nằm trong khoảng từ 1 đến 50.");
    }
}
