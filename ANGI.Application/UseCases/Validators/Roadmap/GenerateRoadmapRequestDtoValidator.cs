using ANGI.Application.DTOs.Roadmap;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Roadmap;

/// <summary>Validates the RM-10 request body before the job is queued.</summary>
public sealed class GenerateRoadmapRequestDtoValidator : AbstractValidator<GenerateRoadmapRequestDto>
{
    public const string ScopeFull = "full";
    public const string ScopeDay = "day";
    public const string ScopeSlot = "slot";
    public const int MaxNoteLength = 500;
    public const int MaxProvinceLength = 100;
    public const decimal MinRadiusKm = 1;
    public const decimal MaxRadiusKm = 50;

    private static readonly HashSet<string> Scopes = new(StringComparer.Ordinal) { ScopeFull, ScopeDay, ScopeSlot };

    /// <summary>Creates the RM-10 scope, origin and constraint rules.</summary>
    public GenerateRoadmapRequestDtoValidator()
    {
        RuleFor(x => x.Scope)
            .NotEmpty().WithMessage("scope là bắt buộc.");
        RuleFor(x => x.Scope)
            .Must(scope => Scopes.Contains(scope))
            .WithMessage("scope chỉ có thể là full, day hoặc slot.")
            .When(x => !string.IsNullOrEmpty(x.Scope));

        RuleFor(x => x.DayId)
            .NotNull().WithMessage("dayId là bắt buộc khi scope là day hoặc slot.")
            .When(x => x.Scope is ScopeDay or ScopeSlot);
        RuleFor(x => x.DayId)
            .GreaterThan(0).WithMessage("dayId phải lớn hơn 0.")
            .When(x => x.DayId.HasValue);

        RuleFor(x => x.MealSlot)
            .NotEmpty().WithMessage("mealSlot là bắt buộc khi scope là slot.")
            .When(x => x.Scope == ScopeSlot);
        RuleFor(x => x.MealSlot)
            .Must(value => GetRoadmapSuggestionsRequestDtoValidator.MealSlots.Contains(value!))
            .WithMessage("mealSlot không hợp lệ.")
            .When(x => !string.IsNullOrEmpty(x.MealSlot));

        RuleFor(x => x.Province)
            .NotEmpty().WithMessage("province là bắt buộc.");
        RuleFor(x => x.Province)
            .MaximumLength(MaxProvinceLength).WithMessage($"province không được vượt quá {MaxProvinceLength} ký tự.")
            .When(x => x.Province is not null);

        RuleFor(x => x.MealSlots)
            .Must(slots => slots!.Length > 0).WithMessage("mealSlots không được rỗng.")
            .Must(slots => slots!.Distinct(StringComparer.Ordinal).Count() == slots!.Length)
            .WithMessage("mealSlots không được trùng nhau.")
            .When(x => x.MealSlots is not null);
        RuleForEach(x => x.MealSlots)
            .Must(value => GetRoadmapSuggestionsRequestDtoValidator.MealSlots.Contains(value))
            .WithMessage("mealSlots chứa bữa không hợp lệ.");

        RuleFor(x => x.MaxPricePerMeal)
            .GreaterThanOrEqualTo(0m).WithMessage("maxPricePerMeal không được âm.")
            .When(x => x.MaxPricePerMeal.HasValue);
        RuleForEach(x => x.TagIds)
            .GreaterThan((short)0).WithMessage("tagIds phải là số dương.");
        RuleForEach(x => x.ExcludeTagIds)
            .GreaterThan((short)0).WithMessage("excludeTagIds phải là số dương.");
        RuleFor(x => x.Note)
            .MaximumLength(MaxNoteLength).WithMessage($"note không được vượt quá {MaxNoteLength} ký tự.")
            .When(x => x.Note is not null);

        RuleFor(x => x.CurrentLatitude)
            .NotNull().WithMessage("Cần gửi cả currentLatitude và currentLongitude.")
            .When(x => x.CurrentLongitude.HasValue);
        RuleFor(x => x.CurrentLongitude)
            .NotNull().WithMessage("Cần gửi cả currentLatitude và currentLongitude.")
            .When(x => x.CurrentLatitude.HasValue);
        RuleFor(x => x.CurrentLatitude)
            .InclusiveBetween(-90m, 90m).WithMessage("currentLatitude phải nằm trong khoảng -90 đến 90.")
            .When(x => x.CurrentLatitude.HasValue);
        RuleFor(x => x.CurrentLongitude)
            .InclusiveBetween(-180m, 180m).WithMessage("currentLongitude phải nằm trong khoảng -180 đến 180.")
            .When(x => x.CurrentLongitude.HasValue);
        RuleFor(x => x.RadiusKm)
            .InclusiveBetween(MinRadiusKm, MaxRadiusKm).WithMessage($"radiusKm phải từ {MinRadiusKm} đến {MaxRadiusKm} km.")
            .When(x => x.RadiusKm.HasValue);
    }
}
