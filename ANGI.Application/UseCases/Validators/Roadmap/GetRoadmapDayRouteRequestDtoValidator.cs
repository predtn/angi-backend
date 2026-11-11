using ANGI.Application.DTOs.Roadmap;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Roadmap;

/// <summary>Validates optional GPS coordinates and the travel profile accepted by RM-14.</summary>
public sealed class GetRoadmapDayRouteRequestDtoValidator : AbstractValidator<GetRoadmapDayRouteRequestDto>
{
    private static readonly string[] _profiles = ["driving", "walking", "cycling"];

    /// <summary>Creates coordinate range, coordinate-pair, and profile rules for RM-14.</summary>
    public GetRoadmapDayRouteRequestDtoValidator()
    {
        RuleFor(request => request.OriginLat)
            .InclusiveBetween(-90m, 90m)
            .WithMessage("Vĩ độ phải nằm trong khoảng -90 đến 90.")
            .When(request => request.OriginLat.HasValue);

        RuleFor(request => request.OriginLng)
            .InclusiveBetween(-180m, 180m)
            .WithMessage("Kinh độ phải nằm trong khoảng -180 đến 180.")
            .When(request => request.OriginLng.HasValue);

        RuleFor(request => request.Profile)
            .Must(BeSupportedProfile)
            .WithMessage("Phương thức di chuyển phải là driving, walking hoặc cycling.");

        RuleFor(request => request).Custom(ValidateCoordinatePair);
    }

    /// <summary>Checks the three route profiles defined by the API contract.</summary>
    private static bool BeSupportedProfile(string? value) =>
        value is not null && _profiles.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Requires GPS latitude and longitude to be supplied together.</summary>
    private static void ValidateCoordinatePair(
        GetRoadmapDayRouteRequestDto request,
        ValidationContext<GetRoadmapDayRouteRequestDto> context)
    {
        if (request.OriginLat.HasValue == request.OriginLng.HasValue)
        {
            return;
        }

        if (!request.OriginLat.HasValue)
        {
            context.AddFailure(nameof(request.OriginLat), "Vĩ độ và kinh độ GPS phải được gửi cùng nhau.");
        }

        if (!request.OriginLng.HasValue)
        {
            context.AddFailure(nameof(request.OriginLng), "Vĩ độ và kinh độ GPS phải được gửi cùng nhau.");
        }
    }
}
