using ANGI.Application.DTOs.Social;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Social;

/// <summary>Validates pagination query parameters for BLOG-03 blog comments listing.</summary>
public sealed class GetBlogCommentsRequestDtoValidator : AbstractValidator<GetBlogCommentsRequestDto>
{
    public GetBlogCommentsRequestDtoValidator()
    {
        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page phải lớn hơn hoặc bằng 1.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("pageSize phải nằm trong khoảng 1 đến 100.");
    }
}
