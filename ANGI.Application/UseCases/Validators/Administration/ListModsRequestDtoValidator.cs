using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Administration;

/// <summary>Validates ADM-M1 filters and pagination boundaries.</summary>
public sealed class ListModsRequestDtoValidator : AbstractValidator<ListModsRequestDto>
{
    /// <summary>Creates validation rules for search, status, and paging.</summary>
    public ListModsRequestDtoValidator()
    {
        RuleFor(request => request.Status)
            .Must(status => status is null or "active" or "deactivated")
            .WithMessage("Trạng thái chỉ có thể là active hoặc deactivated.");
        RuleFor(request => request.Page)
            .GreaterThanOrEqualTo(1).WithMessage("Trang phải lớn hơn hoặc bằng 1.");
        RuleFor(request => request.PageSize)
            .InclusiveBetween(1, 100).WithMessage("Kích thước trang phải từ 1 đến 100.");
    }
}
