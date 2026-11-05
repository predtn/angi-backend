using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Roadmap;

/// <summary>Verifies UpdateRoadmapRequestDtoValidator applies PATCH null semantics and the RM-04 boundaries.</summary>
public sealed class UpdateRoadmapRequestDtoValidatorTests
{
    private readonly UpdateRoadmapRequestDtoValidator _validator = new();

    // TEST-01: Accept an empty partial update because every RM-04 field is optional.
    /// <summary>Verifies omitted PATCH properties are not treated as null assignments.</summary>
    [Fact]
    public async Task Validate_WithNoSpecifiedFields_ShouldBeValid()
    {
        // STEP-01: Assert _validator.ValidateAsync().IsValid is true.
        (await _validator.ValidateAsync(new UpdateRoadmapRequestDto())).IsValid.Should().BeTrue();
    }

    // TEST-02: Accept explicit null for the three nullable scalar fields.
    /// <summary>Verifies startDate, budgetAmount, and originLabel support explicit clearing.</summary>
    [Fact]
    public async Task Validate_WithNullableFieldsCleared_ShouldBeValid()
    {
        // STEP-01: Validate UpdateRoadmapRequestDto { StartDate = null, BudgetAmount = null, OriginLabel = null } and keep result.
        var result = await _validator.ValidateAsync(new UpdateRoadmapRequestDto
        {
            StartDate = null,
            BudgetAmount = null,
            OriginLabel = null
        });

        // STEP-02: Assert result.IsValid is true.
        result.IsValid.Should().BeTrue();
    }

    // TEST-03: Reject null for non-nullable PATCH fields when explicitly supplied.
    /// <summary>Verifies explicit null cannot clear title, numDays, or originMode.</summary>
    [Fact]
    public async Task Validate_WithRequiredPatchFieldsNull_ShouldRejectEachField()
    {
        // STEP-01: Validate UpdateRoadmapRequestDto { Title = null, NumDays = null, OriginMode = null } and keep result.
        var result = await _validator.ValidateAsync(new UpdateRoadmapRequestDto
        {
            Title = null,
            NumDays = null,
            OriginMode = null
        });

        // STEP-02: Assert result.Errors.Select(filtered) contains the expected items.
        result.Errors.Select(error => error.PropertyName).Should().Contain(["Title", "NumDays", "OriginMode"]);
    }

    // TEST-04: Reject every value immediately outside the RM-04 boundaries.
    /// <summary>Verifies partial fields retain the same limits as RM-01.</summary>
    [Fact]
    public async Task Validate_WithOutOfRangeSpecifiedFields_ShouldRejectEachField()
    {
        // STEP-01: Validate UpdateRoadmapRequestDto { Title = a new string, NumDays = 0, BudgetAmount = -1, ... } and keep result.
        var result = await _validator.ValidateAsync(new UpdateRoadmapRequestDto
        {
            Title = new string('a', 201),
            NumDays = 0,
            BudgetAmount = -1,
            OriginMode = "unknown",
            OriginLatitude = -90.000001m,
            OriginLongitude = 180.000001m,
            OriginLabel = new string('b', 201)
        });

        // STEP-02: Assert result.Errors.Select(filtered) contains the expected items.
        result.Errors.Select(error => error.PropertyName).Should().Contain([
            "Title", "NumDays", "BudgetAmount", "OriginMode", "OriginLatitude", "OriginLongitude", "OriginLabel"
        ]);
    }
}
