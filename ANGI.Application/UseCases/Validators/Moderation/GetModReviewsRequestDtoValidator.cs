using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates MOD-F1 pagination and documented search/filter parameters.</summary>
public sealed class GetModReviewsRequestDtoValidator : AbstractValidator<GetModReviewsRequestDto>
{
    public GetModReviewsRequestDtoValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("Trang phải bắt đầu từ 1.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Số phần tử mỗi trang phải từ 1 đến 100.");
        RuleFor(x => x.Status).Must(x => x is null or "visible" or "hidden" or "removed")
            .WithMessage("Trạng thái kiểm duyệt không hợp lệ.");

        When(x => x.Rating.HasValue, () =>
        {
            RuleFor(x => x.Rating!.Value).InclusiveBetween((short)1, (short)5)
                .WithMessage("Đánh giá phải từ 1 đến 5 sao.");
        });

        When(x => x.RestaurantId.HasValue, () =>
        {
            RuleFor(x => x.RestaurantId!.Value).GreaterThan(0)
                .WithMessage("Mã nhà hàng không hợp lệ.");
        });

        When(x => x.UserId.HasValue, () =>
        {
            RuleFor(x => x.UserId!.Value).GreaterThan(0)
                .WithMessage("Mã người dùng không hợp lệ.");
        });
    }
}
