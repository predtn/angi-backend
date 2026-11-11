using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates MOD-V1 verification queue filters and pagination.</summary>
public sealed class ListVerificationsRequestDtoValidator : AbstractValidator<ListVerificationsRequestDto>
{
    private static readonly string[] AllowedStatuses = ["pending", "approved", "rejected", "needs_more_info"];

    /// <summary>Defines the documented status and paging boundaries for MOD-V1.</summary>
    public ListVerificationsRequestDtoValidator()
    {
        RuleFor(request => request.Status)
            .NotEmpty().WithMessage("status không được để trống.");

        RuleFor(request => request.Status)
            .Must(status => AllowedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            .When(request => !string.IsNullOrWhiteSpace(request.Status))
            .WithMessage("status phải là pending, approved, rejected hoặc needs_more_info.");

        RuleFor(request => request.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page phải lớn hơn hoặc bằng 1.");

        RuleFor(request => request.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("pageSize phải nằm trong khoảng 1 đến 100.");
    }
}
