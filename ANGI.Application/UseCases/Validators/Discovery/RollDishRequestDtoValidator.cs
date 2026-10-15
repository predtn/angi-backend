using ANGI.Application.DTOs.Discovery;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Discovery;

/// <summary>Validates the DISH-03 filters.</summary>
public sealed class RollDishRequestDtoValidator : AbstractValidator<RollDishRequestDto>
{
    public const int MaxExcludedDishes = 50;
    public const decimal MinRadiusKm = 0.5m;
    public const decimal MaxRadiusKm = 50;

    public static readonly IReadOnlySet<string> MealTimes = new HashSet<string>(StringComparer.Ordinal)
    {
        "breakfast",
        "lunch",
        "dinner",
        "snack"
    };

    public RollDishRequestDtoValidator()
    {
        RuleFor(x => x.Latitude)
            .NotNull().WithMessage("Cần gửi cả latitude và longitude.")
            .When(x => x.Longitude.HasValue);
        RuleFor(x => x.Longitude)
            .NotNull().WithMessage("Cần gửi cả latitude và longitude.")
            .When(x => x.Latitude.HasValue);
        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90m, 90m).WithMessage("latitude phải nằm trong khoảng -90 đến 90.")
            .When(x => x.Latitude.HasValue);
        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180m, 180m).WithMessage("longitude phải nằm trong khoảng -180 đến 180.")
            .When(x => x.Longitude.HasValue);

        RuleFor(x => x.RadiusKm)
            .Cascade(CascadeMode.Stop)
            .Must((request, _) => request.Latitude.HasValue && request.Longitude.HasValue)
                .WithMessage("radiusKm cần đi kèm latitude và longitude.")
            .InclusiveBetween(MinRadiusKm, MaxRadiusKm)
                .WithMessage($"radiusKm phải từ {MinRadiusKm} đến {MaxRadiusKm} km.")
            .When(x => x.RadiusKm.HasValue);

        RuleFor(x => x.MealTime)
            .Must(mealTime => MealTimes.Contains(mealTime!))
            .WithMessage("mealTime chỉ có thể là breakfast, lunch, dinner hoặc snack.")
            .When(x => x.MealTime is not null);

        RuleFor(x => x.MaxPrice)
            .GreaterThanOrEqualTo(0m).WithMessage("maxPrice không được âm.")
            .When(x => x.MaxPrice.HasValue);

        RuleFor(x => x.ExcludeDishIds)
            .Must(ids => ids!.Length <= MaxExcludedDishes)
            .WithMessage($"excludeDishIds không được vượt quá {MaxExcludedDishes} món.")
            .When(x => x.ExcludeDishIds is not null);
        RuleForEach(x => x.ExcludeDishIds)
            .GreaterThan(0).WithMessage("Mã món phải lớn hơn 0.");
        RuleForEach(x => x.TagIds)
            .GreaterThan((short)0).WithMessage("tagIds phải là số dương.");
        RuleForEach(x => x.ExcludeTagIds)
            .GreaterThan((short)0).WithMessage("excludeTagIds phải là số dương.");
    }
}
