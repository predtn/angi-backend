using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Roadmap;

/// <summary>Verifies CreateRoadmapRequestDtoValidator enforces numeric limits and origin data consistent with the origin mode.</summary>
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
        // STEP-01: Validate CreateRoadmapRequestDto { Title, NumDays, BudgetAmount, ... } and keep result.
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

        // STEP-02: Assert result.IsValid is true.
        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject values immediately outside every numeric and length boundary.
    /// <summary>Verifies all independent RM-01 field limits reject out-of-range input.</summary>
    [Fact]
    public async Task Validate_WithOutOfRangeValues_ShouldReturnEveryRelevantFailure()
    {
        // STEP-01: Validate CreateRoadmapRequestDto { Title = a new string, NumDays = 101, BudgetAmount = -1, ... } and keep result.
        var result = await _validator.ValidateAsync(new CreateRoadmapRequestDto
        {
            Title = new string('a', 201),
            NumDays = 101,
            BudgetAmount = -1,
            OriginMode = "pinned",
            OriginLatitude = 90.000001m,
            OriginLongitude = -180.000001m,
            OriginLabel = new string('b', 201)
        });

        // STEP-02: Assert result.Errors.Select(filtered) contains the expected items.
        result.Errors.Select(error => error.PropertyName).Should().Contain([
            "Title", "NumDays", "BudgetAmount", "OriginLatitude", "OriginLongitude", "OriginLabel"
        ]);
    }

    // TEST-03: Require both coordinates when originMode is pinned.
    /// <summary>Verifies pinned mode cannot violate the database coordinate invariant.</summary>
    [Fact]
    public async Task Validate_WithPinnedOriginWithoutCoordinates_ShouldRejectBothCoordinates()
    {
        // STEP-01: Validate CreateRoadmapRequestDto { Title = "Trip", NumDays = 3, OriginMode = "pinned" } and keep result.
        var result = await _validator.ValidateAsync(new CreateRoadmapRequestDto
        {
            Title = "Trip",
            NumDays = 3,
            OriginMode = "pinned"
        });

        // STEP-02: Assert result.Errors contains a matching item.
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
        // STEP-01: Validate CreateRoadmapRequestDto { Title = "Trip", NumDays = 3, OriginMode = mode, ... } and keep result.
        var result = await _validator.ValidateAsync(new CreateRoadmapRequestDto
        {
            Title = "Trip",
            NumDays = 3,
            OriginMode = mode,
            OriginLatitude = 10,
            OriginLongitude = 106,
            OriginLabel = "Hotel"
        });

        // STEP-02: Assert result.Errors.Select(filtered) contains the expected items.
        result.Errors.Select(error => error.PropertyName).Should().Contain([
            "OriginLatitude", "OriginLongitude", "OriginLabel"
        ]);
    }

    // TEST-05: Default an omitted originMode to a valid none origin.
    /// <summary>Verifies the optional originMode follows the documented none default.</summary>
    [Fact]
    public async Task Validate_WithOmittedOrigin_ShouldBeValid()
    {
        // STEP-01: Validate CreateRoadmapRequestDto { Title = "Trip", NumDays = 1 } and keep result.
        var result = await _validator.ValidateAsync(new CreateRoadmapRequestDto
        {
            Title = "Trip",
            NumDays = 1
        });

        // STEP-02: Assert result.IsValid is true.
        result.IsValid.Should().BeTrue();
    }

    // TEST-06: Reject both required create fields when they contain their CLR defaults.
    /// <summary>Verifies null title and zero numDays cannot pass through model binding defaults.</summary>
    [Fact]
    public async Task Validate_WithMissingRequiredFields_ShouldRejectTitleAndNumDays()
    {
        // STEP-01: Validate an empty CreateRoadmapRequestDto and keep result.
        var result = await _validator.ValidateAsync(new CreateRoadmapRequestDto());

        // STEP-02: Assert result.Errors contains a matching item.
        result.Errors.Should().Contain(error => error.PropertyName == "Title");
        result.Errors.Should().Contain(error => error.PropertyName == "NumDays");
    }

    // Destination province: up to 100 characters, null allowed.
    /// <summary>Verifies the destination province keeps the length of the catalog names.</summary>
    [Fact]
    public void Validate_DestinationProvinceLength_ShouldAllowHundredAndRejectMore()
    {
        var validator = new CreateRoadmapRequestDtoValidator();
        validator.Validate(new CreateRoadmapRequestDto { Title = "Trip", NumDays = 1, DestinationProvince = new string('a', 100) })
            .Errors.Should().NotContain(error => error.PropertyName == "DestinationProvince");
        validator.Validate(new CreateRoadmapRequestDto { Title = "Trip", NumDays = 1, DestinationProvince = null })
            .Errors.Should().NotContain(error => error.PropertyName == "DestinationProvince");
        validator.Validate(new CreateRoadmapRequestDto { Title = "Trip", NumDays = 1, DestinationProvince = new string('a', 101) })
            .Errors.Should().Contain(error => error.PropertyName == "DestinationProvince");
    }
}
