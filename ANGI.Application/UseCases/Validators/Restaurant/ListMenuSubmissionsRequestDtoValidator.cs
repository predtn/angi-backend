using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates OWN-12 filters and pagination bounds.</summary>
public sealed class ListMenuSubmissionsRequestDtoValidator : AbstractValidator<ListMenuSubmissionsRequestDto>
{
    private static readonly string[] Statuses = ["draft", "pending", "approved", "rejected", "cancelled"];

    /// <summary>Creates status, page, and page-size validation rules.</summary>
    public ListMenuSubmissionsRequestDtoValidator()
    {
        RuleFor(request => request.Status)
            .Must(status => status is null || Statuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Trạng thái bản menu không hợp lệ.");
        RuleFor(request => request.Page)
            .GreaterThanOrEqualTo(1).WithMessage("Trang phải bắt đầu từ 1.");
        RuleFor(request => request.PageSize)
            .InclusiveBetween(1, 100).WithMessage("Kích thước trang phải từ 1 đến 100.");
    }
}
