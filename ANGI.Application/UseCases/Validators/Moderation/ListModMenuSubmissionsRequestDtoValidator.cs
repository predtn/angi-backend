using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates MOD-M1 status, identifier and safe pagination bounds.</summary>
public sealed class ListModMenuSubmissionsRequestDtoValidator : AbstractValidator<ListModMenuSubmissionsRequestDto>
{
    /// <summary>Defines the documented queue filters and paging limits.</summary>
    public ListModMenuSubmissionsRequestDtoValidator()
    {
        RuleFor(x => x.Status).NotEmpty().WithMessage("Trạng thái không được để trống.");
        RuleFor(x => x.Status).Must(x => x is "pending" or "approved" or "rejected")
            .When(x => !string.IsNullOrWhiteSpace(x.Status)).WithMessage("Trạng thái không hợp lệ.");
        RuleFor(x => x.RestaurantId).GreaterThan(0).When(x => x.RestaurantId.HasValue)
            .WithMessage("Mã nhà hàng phải lớn hơn 0.");
        RuleFor(x => x.Page).GreaterThan(0).WithMessage("Trang phải lớn hơn 0.")
            .Must((x, page) => (long)(page - 1) * x.PageSize <= int.MaxValue)
            .WithMessage("Trang vượt quá giới hạn.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Kích thước trang từ 1 đến 100.");
    }
}
