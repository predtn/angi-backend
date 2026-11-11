using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates incoming query parameters for REST-01 restaurant browsing.</summary>
public sealed class BrowseRestaurantsRequestDtoValidator : AbstractValidator<BrowseRestaurantsRequestDto>
{
    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "rating",
        "distance",
        "newest"
    };

    /// <summary>Enforces contract rules and parameter dependencies defined in the API design.</summary>
    public BrowseRestaurantsRequestDtoValidator()
    {
        RuleFor(x => x.PriceLevel)
            .InclusiveBetween((short)1, (short)4)
            .WithMessage("Mức giá phải từ 1 đến 4.")
            .When(x => x.PriceLevel.HasValue);

        RuleFor(x => x.MinRating)
            .InclusiveBetween(1m, 5m)
            .WithMessage("Điểm đánh giá phải từ 1 đến 5.")
            .When(x => x.MinRating.HasValue);

        RuleFor(x => x.Lat)
            .InclusiveBetween(-90m, 90m)
            .WithMessage("Vĩ độ phải nằm trong khoảng -90 đến 90.")
            .When(x => x.Lat.HasValue);

        RuleFor(x => x.Lng)
            .InclusiveBetween(-180m, 180m)
            .WithMessage("Kinh độ phải nằm trong khoảng -180 đến 180.")
            .When(x => x.Lng.HasValue);

        RuleFor(x => x.RadiusKm)
            .GreaterThan(0m)
            .WithMessage("Bán kính tìm kiếm phải lớn hơn 0.")
            .LessThanOrEqualTo(50m)
            .WithMessage("Bán kính tìm kiếm không được vượt quá 50 km.")
            .When(x => x.RadiusKm.HasValue);

        RuleFor(x => x.Lat)
            .NotNull()
            .WithMessage("Cần cung cấp tọa độ lat và lng khi tìm kiếm theo bán kính.")
            .When(x => x.RadiusKm.HasValue);

        RuleFor(x => x.Lng)
            .NotNull()
            .WithMessage("Cần cung cấp tọa độ lat và lng khi tìm kiếm theo bán kính.")
            .When(x => x.RadiusKm.HasValue);

        RuleFor(x => x.Sort)
            .Must(s => string.IsNullOrWhiteSpace(s) || AllowedSortFields.Contains(s))
            .WithMessage("Tiêu chí sắp xếp phải là rating, distance hoặc newest.")
            .When(x => !string.IsNullOrWhiteSpace(x.Sort));

        RuleFor(x => x.Lat)
            .NotNull()
            .WithMessage("Thiếu tọa độ lat khi sắp xếp theo khoảng cách.")
            .When(x => string.Equals(x.Sort, "distance", StringComparison.OrdinalIgnoreCase));

        RuleFor(x => x.Lng)
            .NotNull()
            .WithMessage("Thiếu tọa độ lng khi sắp xếp theo khoảng cách.")
            .When(x => string.Equals(x.Sort, "distance", StringComparison.OrdinalIgnoreCase));

        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Trang phải bắt đầu từ 1.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("Số lượng phần tử mỗi trang phải từ 1 đến 100.");
    }
}
