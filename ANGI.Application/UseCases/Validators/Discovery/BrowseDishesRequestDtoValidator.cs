using ANGI.Application.DTOs.Discovery;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Discovery;

/// <summary>Validates the DISH-01 filters; the cursor itself is checked by the use case (INVALID_CURSOR).</summary>
public sealed class BrowseDishesRequestDtoValidator : AbstractValidator<BrowseDishesRequestDto>
{
    public const int MaxLimit = 50;
    public const decimal MaxRadiusKm = 50;

    public static readonly IReadOnlySet<string> DishForms = new HashSet<string>(StringComparer.Ordinal)
    {
        "soup",
        "dry",
        "other"
    };

    public BrowseDishesRequestDtoValidator()
    {
        RuleFor(x => x.Limit)
            .InclusiveBetween(1, MaxLimit).WithMessage($"limit phải từ 1 đến {MaxLimit}.")
            .When(x => x.Limit.HasValue);

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
            .Cascade(CascadeMode.Stop)
            .Must((request, _) => request.Lat.HasValue && request.Lng.HasValue)
                .WithMessage("radiusKm cần đi kèm lat và lng.")
            .GreaterThan(0m).WithMessage("radiusKm phải lớn hơn 0.")
            .LessThanOrEqualTo(MaxRadiusKm).WithMessage($"radiusKm không được vượt quá {MaxRadiusKm} km.")
            .When(x => x.RadiusKm.HasValue);

        RuleFor(x => x.MinPrice)
            .GreaterThanOrEqualTo(0m).WithMessage("minPrice không được âm.")
            .When(x => x.MinPrice.HasValue);
        RuleFor(x => x.MaxPrice)
            .GreaterThanOrEqualTo(0m).WithMessage("maxPrice không được âm.")
            .When(x => x.MaxPrice.HasValue);
        RuleFor(x => x.MaxPrice)
            .Must((request, maxPrice) => maxPrice >= request.MinPrice)
            .WithMessage("maxPrice phải lớn hơn hoặc bằng minPrice.")
            .When(x => x.MinPrice.HasValue && x.MaxPrice.HasValue);

        RuleFor(x => x.DishForm)
            .Must(form => DishForms.Contains(form!))
            .WithMessage("dishForm chỉ có thể là soup, dry hoặc other.")
            .When(x => x.DishForm is not null);

        RuleForEach(x => x.TagIds)
            .GreaterThan((short)0).WithMessage("tagIds phải là số dương.");
        RuleForEach(x => x.ExcludeTagIds)
            .GreaterThan((short)0).WithMessage("excludeTagIds phải là số dương.");

        RuleFor(x => x.RestaurantId)
            .GreaterThan(0).WithMessage("restaurantId phải lớn hơn 0.")
            .When(x => x.RestaurantId.HasValue);
    }
}
