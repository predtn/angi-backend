using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Roadmap;

public sealed class CreateRoadmapRequestDtoValidatorTests
{
    private readonly CreateRoadmapRequestDtoValidator _validator = new();

    // TEST-01: Accept all inclusive numeric boundaries and a complete pinned origin.
    /// <summary>Verifies the documented lower and upper boundaries are inclusive.</summary>
    [Theory]
    [InlineData(1, 0, -90, -180)]
    [InlineData(30, 999999999, 90, 180)]
    public async Task Validate_WithBoundaryValues_ShouldBeValid(short numDays, decimal budget, decimal latitude, decimal longitude)
    {
        var result = await _validator.ValidateAsync(new CreateRoadmapRequestDto
        {
            Title = new string('a', 200),
            NumDays = numDays,
            BudgetAmount = budget,
            OriginMode = "pinned",
            OriginLatitude = latitude,
            OriginLongitude = longitude,
            OriginLabel = new string('b', 200)
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject values immediately outside every numeric and length boundary.
    /// <summary>Verifies all independent RM-01 field limits reject out-of-range input.</summary>
    [Fact]
    public async Task Validate_WithOutOfRangeValues_ShouldReturnEveryRelevantFailure()
    {
        var result = await _validator.ValidateAsync(new CreateRoadmapRequestDto
        {
            Title = new string('a', 201),
            NumDays = 31,
            BudgetAmount = -1,
            OriginMode = "pinned",
            OriginLatitude = 90.000001m,
            OriginLongitude = -180.000001m,
            OriginLabel = new string('b', 201)
        });

        result.Errors.Select(error => error.PropertyName).Should().Contain([
            "Title", "NumDays", "BudgetAmount", "OriginLatitude", "OriginLongitude", "OriginLabel"
        ]);
    }

    // TEST-03: Require both coordinates when originMode is pinned.
    /// <summary>Verifies pinned mode cannot violate the database coordinate invariant.</summary>
    [Fact]
    public async Task Validate_WithPinnedOriginWithoutCoordinates_ShouldRejectBothCoordinates()
    {
        var result = await _validator.ValidateAsync(new CreateRoadmapRequestDto
        {
            Title = "Trip",
            NumDays = 3,
            OriginMode = "pinned"
        });

        result.Errors.Should().Contain(error => error.PropertyName == "OriginLatitude");
        result.Errors.Should().Contain(error => error.PropertyName == "OriginLongitude");
    }

    // TEST-04: Reject persisted origin data when originMode is none or gps.
    /// <summary>Verifies non-pinned modes reject coordinates and a nonblank label.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("gps")]
    public async Task Validate_WithNonPinnedStoredOrigin_ShouldRejectOriginFields(string mode)
    {
        var result = await _validator.ValidateAsync(new CreateRoadmapRequestDto
        {
            Title = "Trip",
            NumDays = 3,
            OriginMode = mode,
            OriginLatitude = 10,
            OriginLongitude = 106,
            OriginLabel = "Hotel"
        });

        result.Errors.Select(error => error.PropertyName).Should().Contain([
            "OriginLatitude", "OriginLongitude", "OriginLabel"
        ]);
    }

    // TEST-05: Default an omitted originMode to a valid none origin.
    /// <summary>Verifies the optional originMode follows the documented none default.</summary>
    [Fact]
    public async Task Validate_WithOmittedOrigin_ShouldBeValid()
    {
        var result = await _validator.ValidateAsync(new CreateRoadmapRequestDto
        {
            Title = "Trip",
            NumDays = 1
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-06: Reject both required create fields when they contain their CLR defaults.
    /// <summary>Verifies null title and zero numDays cannot pass through model binding defaults.</summary>
    [Fact]
    public async Task Validate_WithMissingRequiredFields_ShouldRejectTitleAndNumDays()
    {
        var result = await _validator.ValidateAsync(new CreateRoadmapRequestDto());

        result.Errors.Should().Contain(error => error.PropertyName == "Title");
        result.Errors.Should().Contain(error => error.PropertyName == "NumDays");
    }
}
