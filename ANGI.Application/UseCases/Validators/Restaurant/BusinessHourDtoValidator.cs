using System.Globalization;
using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates a single restaurant opening interval.</summary>
public sealed class BusinessHourDtoValidator : AbstractValidator<BusinessHourDto>
{
    /// <summary>Creates day-of-week and HH:mm validation rules.</summary>
    public BusinessHourDtoValidator()
    {
        RuleFor(x => x.DayOfWeek).InclusiveBetween((short)0, (short)6);
        RuleFor(x => x.OpenTime).Must(BeValidTime).WithMessage("OpenTime must use HH:mm format.");
        RuleFor(x => x.CloseTime).Must(BeValidTime).WithMessage("CloseTime must use HH:mm format.");
        RuleFor(x => x)
            .Must(hour => !string.Equals(hour.OpenTime, hour.CloseTime, StringComparison.Ordinal))
            .WithMessage("OpenTime and CloseTime must be different.");
    }

    /// <summary>Checks whether a value is an exact 24-hour HH:mm time.</summary>
    private static bool BeValidTime(string value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
