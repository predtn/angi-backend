using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates MOD-U1 paging and filter values.</summary>
public sealed class ListUsersRequestDtoValidator : AbstractValidator<ListUsersRequestDto>
{
    public ListUsersRequestDtoValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("Trang phải bắt đầu từ 1.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Số phần tử mỗi trang phải từ 1 đến 100.");
        RuleFor(x => x.Role)
            .Must(x => x is null or "TRAVELER" or "RESTAURANT_OWNER")
            .WithMessage("Vai trò người dùng không hợp lệ.");
        RuleFor(x => x.Status)
            .Must(x => x is null or "pending_verification" or "active" or "suspended" or "banned" or "deactivated")
            .WithMessage("Trạng thái người dùng không hợp lệ.");
    }
}
