using ANGI.Application.DTOs.Roadmap;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Roadmap;

/// <summary>Validates optional RM-08 replacement and destination values before persistence starts.</summary>
public sealed class UpdateRoadmapItemRequestDtoValidator : AbstractValidator<UpdateRoadmapItemRequestDto>
{
    /// <summary>Creates positive-id and documented meal-slot rules for RM-08.</summary>
    public UpdateRoadmapItemRequestDtoValidator()
    {
        RuleFor(x => x.DishId)
            .GreaterThan(0).WithMessage("dishId phải lớn hơn 0.")
            .When(x => x.DishId.HasValue);

        RuleFor(x => x.DayId)
            .GreaterThan(0).WithMessage("dayId phải lớn hơn 0.")
            .When(x => x.DayId.HasValue);

        RuleFor(x => x.MealSlot)
            .Must(value => GetRoadmapSuggestionsRequestDtoValidator.MealSlots.Contains(value!))
            .WithMessage("mealSlot không hợp lệ.")
            .When(x => x.MealSlot is not null);
    }
}
