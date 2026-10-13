using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates parameters for REST-03 restaurant menu request.</summary>
public sealed class GetRestaurantMenuRequestDtoValidator : AbstractValidator<GetRestaurantMenuRequestDto>
{
    public GetRestaurantMenuRequestDtoValidator()
    {
        RuleFor(x => x.RestaurantId)
            .GreaterThan(0)
            .WithMessage("restaurantId phải lớn hơn 0.");
    }
}
