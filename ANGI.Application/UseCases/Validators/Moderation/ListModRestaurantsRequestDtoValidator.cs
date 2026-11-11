using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates MOD-R1 paging and exact documented filter values.</summary>
public sealed class ListModRestaurantsRequestDtoValidator : AbstractValidator<ListModRestaurantsRequestDto>
{
    /// <summary>Rejects invalid enum filters, pages and page sizes before querying persistence.</summary>
    public ListModRestaurantsRequestDtoValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("Trang phải bắt đầu từ 1.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Số phần tử mỗi trang phải từ 1 đến 100.");
        RuleFor(x => x.VerificationStatus).Must(x => x is null or "unverified" or "pending" or "verified" or "rejected")
            .WithMessage("Trạng thái xác minh không hợp lệ.");
        RuleFor(x => x.OperatingStatus).Must(x => x is null or "open" or "temporarily_closed" or "permanently_closed")
            .WithMessage("Trạng thái hoạt động không hợp lệ.");
        RuleFor(x => x.ModerationStatus).Must(x => x is null or "visible" or "hidden" or "suspended")
            .WithMessage("Trạng thái kiểm duyệt không hợp lệ.");
    }
}
