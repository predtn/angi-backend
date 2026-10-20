using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Roadmap;

public sealed class GetRoadmapDayRouteRequestDtoValidatorTests
{
    private readonly GetRoadmapDayRouteRequestDtoValidator _validator = new();

    // TEST-01: Accept no GPS coordinates and every documented travel profile.
    /// <summary>Verifies the valid RM-14 profile regions and the optional-origin path.</summary>
    [Theory]
    [InlineData("driving")]
    [InlineData("walking")]
    [InlineData("cycling")]
    [InlineData("CYCLING")]
    public async Task Validate_WithSupportedProfile_ShouldSucceed(string profile)
    {
        var result = await _validator.ValidateAsync(new GetRoadmapDayRouteRequestDto { Profile = profile });

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Accept both inclusive coordinate boundaries when GPS is complete.
    /// <summary>Verifies the minimum and maximum valid WGS84 coordinate pairs.</summary>
    [Theory]
    [InlineData(-90, -180)]
    [InlineData(90, 180)]
    public async Task Validate_WithCoordinateBoundaries_ShouldSucceed(decimal latitude, decimal longitude)
    {
        var result = await _validator.ValidateAsync(new GetRoadmapDayRouteRequestDto
        {
            OriginLat = latitude,
            OriginLng = longitude
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-03: Reject the first values outside latitude and longitude boundaries.
    /// <summary>Verifies all invalid coordinate range regions.</summary>
    [Theory]
    [InlineData(-90.000001, 0, "OriginLat")]
    [InlineData(90.000001, 0, "OriginLat")]
    [InlineData(0, -180.000001, "OriginLng")]
    [InlineData(0, 180.000001, "OriginLng")]
    public async Task Validate_WithCoordinateOutsideBoundary_ShouldFail(
        decimal latitude,
        decimal longitude,
        string propertyName)
    {
        var result = await _validator.ValidateAsync(new GetRoadmapDayRouteRequestDto
        {
            OriginLat = latitude,
            OriginLng = longitude
        });

        result.Errors.Should().Contain(error => error.PropertyName == propertyName);
    }

    // TEST-04: Reject either half of an incomplete GPS coordinate pair.
    /// <summary>Verifies both asymmetric coordinate-pair failure branches.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Validate_WithOnlyOneCoordinate_ShouldFailOnMissingCoordinate(bool hasLatitude)
    {
        var request = hasLatitude
            ? new GetRoadmapDayRouteRequestDto { OriginLat = 10m }
            : new GetRoadmapDayRouteRequestDto { OriginLng = 106m };

        var result = await _validator.ValidateAsync(request);

        result.Errors.Should().ContainSingle(error =>
            error.PropertyName == (hasLatitude ? "OriginLng" : "OriginLat"));
    }

    // TEST-05: Reject null, blank, and unknown route profiles.
    /// <summary>Verifies every invalid profile shape produces one profile validation error.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("flying")]
    public async Task Validate_WithUnsupportedProfile_ShouldFail(string? profile)
    {
        var result = await _validator.ValidateAsync(new GetRoadmapDayRouteRequestDto { Profile = profile });

        result.Errors.Should().ContainSingle(error => error.PropertyName == "Profile");
    }
}
