using ANGI.Application.DTOs.Roadmap;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Roadmap;

/// <summary>Validates the RM-07 request body before the atomic add operation starts.</summary>
public sealed class AddRoadmapItemRequestDtoValidator : AbstractValidator<AddRoadmapItemRequestDto>
{
    /// <summary>Creates required-id and meal-slot rules for RM-07.</summary>
    public AddRoadmapItemRequestDtoValidator()
    {
        RuleFor(x => x.DishId)
            .GreaterThan(0).WithMessage("dishId phải lớn hơn 0.");

        RuleFor(x => x.MealSlot)
            .NotEmpty().WithMessage("mealSlot là bắt buộc.");
        RuleFor(x => x.MealSlot)
            .Must(value => GetRoadmapSuggestionsRequestDtoValidator.MealSlots.Contains(value!))
            .WithMessage("mealSlot không hợp lệ.")
            .When(x => !string.IsNullOrWhiteSpace(x.MealSlot));
    }
}
