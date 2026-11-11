using ANGI.Application.DTOs.Roadmap;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Roadmap;

/// <summary>Validates the documented RM-06 filters before roadmap or dish data is loaded.</summary>
public sealed class GetRoadmapSuggestionsRequestDtoValidator : AbstractValidator<GetRoadmapSuggestionsRequestDto>
{
    public const int MaxLimit = 50;
    public const decimal MaxRadiusKm = 50;

    public static readonly IReadOnlySet<string> MealSlots = new HashSet<string>(StringComparer.Ordinal)
    {
        "breakfast",
        "lunch",
        "dinner",
        "snack",
        "late_night"
    };

    /// <summary>Creates all field and cross-field rules for RM-06.</summary>
    public GetRoadmapSuggestionsRequestDtoValidator()
    {
        RuleFor(x => x.Province)
            .NotEmpty().WithMessage("province là bắt buộc.");

        RuleFor(x => x.DayId)
            .GreaterThan(0).WithMessage("dayId phải lớn hơn 0.")
            .When(x => x.DayId.HasValue);

        RuleFor(x => x.MealSlot)
            .Must(value => MealSlots.Contains(value!))
            .WithMessage("mealSlot không hợp lệ.")
            .When(x => x.MealSlot is not null);

        RuleFor(x => x.MaxPrice)
            .GreaterThanOrEqualTo(0).WithMessage("maxPrice không được âm.")
            .When(x => x.MaxPrice.HasValue);

        RuleFor(x => x.Lat)
            .NotNull().WithMessage("Cần gửi cả lat và lng.")
            .When(x => x.Lng.HasValue);
        RuleFor(x => x.Lng)
            .NotNull().WithMessage("Cần gửi cả lat và lng.")
            .When(x => x.Lat.HasValue);
        RuleFor(x => x.Lat)
            .InclusiveBetween(-90m, 90m).WithMessage("lat phải nằm trong khoảng -90 đến 90.")
            .When(x => x.Lat.HasValue);
        RuleFor(x => x.Lng)
            .InclusiveBetween(-180m, 180m).WithMessage("lng phải nằm trong khoảng -180 đến 180.")
            .When(x => x.Lng.HasValue);

        RuleFor(x => x.RadiusKm)
            .GreaterThan(0).WithMessage("radiusKm phải lớn hơn 0.")
            .LessThanOrEqualTo(MaxRadiusKm).WithMessage($"radiusKm không được vượt quá {MaxRadiusKm} km.")
            .When(x => x.RadiusKm.HasValue);

        RuleFor(x => x.Limit)
            .InclusiveBetween(1, MaxLimit).WithMessage($"limit phải từ 1 đến {MaxLimit}.")
            .When(x => x.Limit.HasValue);

        RuleForEach(x => x.TagIds)
            .GreaterThan((short)0).WithMessage("tagIds phải là số dương.");
        RuleForEach(x => x.ExcludeTagIds)
            .GreaterThan((short)0).WithMessage("excludeTagIds phải là số dương.");
    }
}
