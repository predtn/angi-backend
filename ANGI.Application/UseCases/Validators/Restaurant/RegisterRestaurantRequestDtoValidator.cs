using System.Globalization;
using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates the complete OWN-01 restaurant registration request.</summary>
public sealed class RegisterRestaurantRequestDtoValidator : AbstractValidator<RegisterRestaurantRequestDto>
{
    /// <summary>Creates validation rules matching the OWN-01 API contract.</summary>
    public RegisterRestaurantRequestDtoValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Phone).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Website).Must(BeHttpUrl).When(x => !string.IsNullOrWhiteSpace(x.Website));
        RuleFor(x => x.AddressLine).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Ward).MaximumLength(100);
        RuleFor(x => x.District).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ProvinceName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Latitude).NotNull().InclusiveBetween(-90m, 90m);
        RuleFor(x => x.Longitude).NotNull().InclusiveBetween(-180m, 180m);
        RuleFor(x => x.PriceLevel).InclusiveBetween((short)1, (short)4).When(x => x.PriceLevel.HasValue);
        RuleFor(x => x.ImageMediaIds)
            .Must(ids => ids is null || ids.Count <= 10)
            .WithMessage("ImageMediaIds must contain at most 10 items.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("ImageMediaIds must not contain duplicates.");
        RuleForEach(x => x.BusinessHours).SetValidator(new BusinessHourDtoValidator());
        RuleFor(x => x.BusinessHours)
            .Must(HaveNonOverlappingIntervals)
            .WithMessage("BusinessHours must not overlap.");
    }

    /// <summary>Checks whether the website is an absolute HTTP or HTTPS URL.</summary>
    private static bool BeHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Prevents overlaps across the circular seven-day schedule, including overnight intervals.</summary>
    private static bool HaveNonOverlappingIntervals(IReadOnlyList<BusinessHourDto>? hours)
    {
        if (hours is null)
        {
            return true;
        }

        const int minutesPerDay = 24 * 60;
        const int minutesPerWeek = 7 * minutesPerDay;
        var intervals = new List<(int Start, int End)>();

        foreach (var hour in hours)
        {
            if (!TimeOnly.TryParseExact(
                    hour.OpenTime,
                    "HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var openTime) ||
                !TimeOnly.TryParseExact(
                    hour.CloseTime,
                    "HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var closeTime))
            {
                continue;
            }

            var start = (hour.DayOfWeek * minutesPerDay) + (openTime.Hour * 60) + openTime.Minute;
            var end = (hour.DayOfWeek * minutesPerDay) + (closeTime.Hour * 60) + closeTime.Minute;
            if (end <= start)
            {
                end += minutesPerDay;
            }

            intervals.Add((start, end));
        }

        for (var firstIndex = 0; firstIndex < intervals.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < intervals.Count; secondIndex++)
            {
                var first = intervals[firstIndex];
                var second = intervals[secondIndex];
                foreach (var weekShift in new[] { -minutesPerWeek, 0, minutesPerWeek })
                {
                    var shiftedStart = second.Start + weekShift;
                    var shiftedEnd = second.End + weekShift;
                    if (first.Start < shiftedEnd && shiftedStart < first.End)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
