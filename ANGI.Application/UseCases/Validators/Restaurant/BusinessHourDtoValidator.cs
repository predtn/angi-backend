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
        RuleFor(x => x.DayOfWeek)
            .InclusiveBetween((short)0, (short)6).WithMessage("Thứ trong tuần phải từ 0 (Chủ nhật) đến 6 (Thứ bảy).");
        RuleFor(x => x.OpenTime).Must(BeValidTime).WithMessage("Giờ mở cửa phải có dạng HH:mm.");
        RuleFor(x => x.CloseTime).Must(BeValidTime).WithMessage("Giờ đóng cửa phải có dạng HH:mm.");
        RuleFor(x => x)
            .Must(hour => !string.Equals(hour.OpenTime, hour.CloseTime, StringComparison.Ordinal))
            .WithMessage("Giờ mở cửa và giờ đóng cửa phải khác nhau.");
    }

    /// <summary>Checks whether a value is an exact 24-hour HH:mm time.</summary>
    private static bool BeValidTime(string value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
