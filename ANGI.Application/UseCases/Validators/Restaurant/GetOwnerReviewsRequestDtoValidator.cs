using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates parameters for OWN-19 owner reviews request.</summary>
public sealed class GetOwnerReviewsRequestDtoValidator : AbstractValidator<GetOwnerReviewsRequestDto>
{
    public GetOwnerReviewsRequestDtoValidator()
    {
        RuleFor(x => x.Rating)
            .InclusiveBetween((short)1, (short)5)
            .When(x => x.Rating.HasValue)
            .WithMessage("rating phải từ 1 đến 5 sao.");

        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page phải lớn hơn hoặc bằng 1.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("pageSize phải nằm trong khoảng 1 đến 100.");
    }
}
