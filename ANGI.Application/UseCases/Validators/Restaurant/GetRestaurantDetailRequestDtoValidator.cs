using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates parameters for REST-02 restaurant detail request.</summary>
public sealed class GetRestaurantDetailRequestDtoValidator : AbstractValidator<GetRestaurantDetailRequestDto>
{
    public GetRestaurantDetailRequestDtoValidator()
    {
        RuleFor(x => x.RestaurantId)
            .GreaterThan(0)
            .WithMessage("restaurantId phải lớn hơn 0.");

        RuleFor(x => x.Lat)
            .InclusiveBetween(-90m, 90m)
            .When(x => x.Lat.HasValue)
            .WithMessage("lat phải nằm trong khoảng -90 đến 90.");

        RuleFor(x => x.Lng)
            .InclusiveBetween(-180m, 180m)
            .When(x => x.Lng.HasValue)
            .WithMessage("lng phải nằm trong khoảng -180 đến 180.");
    }
}
