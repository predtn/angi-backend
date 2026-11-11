using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates MOD-B1 query parameters for paging and documented filters.</summary>
public sealed class GetModBlogsRequestDtoValidator : AbstractValidator<GetModBlogsRequestDto>
{
    /// <summary>Enforces 1-based paging and valid documented blog status values.</summary>
    public GetModBlogsRequestDtoValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("Trang phải bắt đầu từ 1.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Số phần tử mỗi trang phải từ 1 đến 100.");
        RuleFor(x => x.Status).Must(x => x is null or "published" or "hidden" or "removed")
            .WithMessage("Trạng thái bài viết không hợp lệ.");
        RuleFor(x => x.AuthorId).GreaterThan(0).WithMessage("Mã tác giả phải lớn hơn 0.").When(x => x.AuthorId.HasValue);
    }
}
