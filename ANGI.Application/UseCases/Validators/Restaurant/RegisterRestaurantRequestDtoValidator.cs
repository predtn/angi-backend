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
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Tên nhà hàng là bắt buộc.")
            .MaximumLength(200).WithMessage("Tên nhà hàng không được vượt quá 200 ký tự.");
        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Mô tả không được vượt quá 2000 ký tự.");
        RuleFor(x => x.Phone)
            .NotEmpty().WithMessage("Số điện thoại là bắt buộc.")
            .MaximumLength(20).WithMessage("Số điện thoại không được vượt quá 20 ký tự.");
        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Email không đúng định dạng.")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Website)
            .Must(BeHttpUrl).WithMessage("Website phải là URL http hoặc https.")
            .When(x => !string.IsNullOrWhiteSpace(x.Website));
        RuleFor(x => x.AddressLine)
            .NotEmpty().WithMessage("Địa chỉ là bắt buộc.")
            .MaximumLength(255).WithMessage("Địa chỉ không được vượt quá 255 ký tự.");
        RuleFor(x => x.Ward)
            .MaximumLength(100).WithMessage("Phường/xã không được vượt quá 100 ký tự.");
        RuleFor(x => x.District)
            .NotEmpty().WithMessage("Quận/huyện là bắt buộc.")
            .MaximumLength(100).WithMessage("Quận/huyện không được vượt quá 100 ký tự.");
        RuleFor(x => x.ProvinceName)
            .NotEmpty().WithMessage("Tỉnh/thành là bắt buộc.")
            .MaximumLength(100).WithMessage("Tỉnh/thành không được vượt quá 100 ký tự.");
        RuleFor(x => x.Latitude)
            .NotNull().WithMessage("Vĩ độ là bắt buộc.")
            .InclusiveBetween(-90m, 90m).WithMessage("Vĩ độ phải nằm trong khoảng -90 đến 90.");
        RuleFor(x => x.Longitude)
            .NotNull().WithMessage("Kinh độ là bắt buộc.")
            .InclusiveBetween(-180m, 180m).WithMessage("Kinh độ phải nằm trong khoảng -180 đến 180.");
        RuleFor(x => x.PriceLevel)
            .InclusiveBetween((short)1, (short)4).WithMessage("Mức giá phải từ 1 đến 4.")
            .When(x => x.PriceLevel.HasValue);
        RuleFor(x => x.ImageMediaIds)
            .Must(ids => ids is null || ids.Count <= 10)
            .WithMessage("Chỉ được chọn tối đa 10 ảnh.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Danh sách ảnh không được trùng nhau.");
        RuleForEach(x => x.BusinessHours).SetValidator(new BusinessHourDtoValidator());
        RuleFor(x => x.BusinessHours)
            .Must(HaveNonOverlappingIntervals)
            .WithMessage("Các khung giờ mở cửa không được chồng lên nhau.");
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
