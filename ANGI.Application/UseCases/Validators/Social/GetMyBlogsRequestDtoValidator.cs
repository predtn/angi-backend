using ANGI.Application.DTOs.Social;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Social;

/// <summary>Validates pagination and status filter query parameters for BLOG-05.</summary>
public sealed class GetMyBlogsRequestDtoValidator : AbstractValidator<GetMyBlogsRequestDto>
{
    private static readonly string[] ValidStatuses = ["published", "hidden", "removed"];

    /// <summary>Enforces 1-based pagination, maximum page size, and recognized status filter values.</summary>
    public GetMyBlogsRequestDtoValidator()
    {
        RuleFor(request => request.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Trang phải bắt đầu từ 1.");

        RuleFor(request => request.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("Số lượng phần tử mỗi trang phải từ 1 đến 100.");

        RuleFor(request => request.Status)
            .Must(status => ValidStatuses.Contains(status!.Trim().ToLowerInvariant()))
            .When(request => !string.IsNullOrWhiteSpace(request.Status))
            .WithMessage("Trạng thái blog không hợp lệ. Chỉ chấp nhận published, hidden hoặc removed.");
    }
}
