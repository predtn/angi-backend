using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates parameters for REST-04 restaurant reviews request.</summary>
public sealed class GetRestaurantReviewsRequestDtoValidator : AbstractValidator<GetRestaurantReviewsRequestDto>
{
    private static readonly string[] AllowedSorts = { "newest", "rating_desc", "rating_asc" };

    public GetRestaurantReviewsRequestDtoValidator()
    {
        RuleFor(x => x.RestaurantId)
            .GreaterThan(0)
            .WithMessage("restaurantId phải lớn hơn 0.");

        RuleFor(x => x.Rating)
            .InclusiveBetween((short)1, (short)5)
            .When(x => x.Rating.HasValue)
            .WithMessage("rating phải từ 1 đến 5 sao.");

        RuleFor(x => x.Sort)
            .Must(s => s == null || AllowedSorts.Contains(s))
            .WithMessage("sort chỉ nhận: newest, rating_desc, rating_asc.");

        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page phải lớn hơn hoặc bằng 1.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("pageSize phải nằm trong khoảng 1 đến 100.");
    }
}
