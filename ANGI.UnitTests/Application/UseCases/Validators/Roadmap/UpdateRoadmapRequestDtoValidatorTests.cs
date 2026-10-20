using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Roadmap;

public sealed class UpdateRoadmapRequestDtoValidatorTests
{
    private readonly UpdateRoadmapRequestDtoValidator _validator = new();

    // TEST-01: Accept an empty partial update because every RM-04 field is optional.
    /// <summary>Verifies omitted PATCH properties are not treated as null assignments.</summary>
    [Fact]
    public async Task Validate_WithNoSpecifiedFields_ShouldBeValid()
    {
        (await _validator.ValidateAsync(new UpdateRoadmapRequestDto())).IsValid.Should().BeTrue();
    }

    // TEST-02: Accept explicit null for the three nullable scalar fields.
    /// <summary>Verifies startDate, budgetAmount, and originLabel support explicit clearing.</summary>
    [Fact]
    public async Task Validate_WithNullableFieldsCleared_ShouldBeValid()
    {
        var result = await _validator.ValidateAsync(new UpdateRoadmapRequestDto
        {
            StartDate = null,
            BudgetAmount = null,
            OriginLabel = null
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-03: Reject null for non-nullable PATCH fields when explicitly supplied.
    /// <summary>Verifies explicit null cannot clear title, numDays, or originMode.</summary>
    [Fact]
    public async Task Validate_WithRequiredPatchFieldsNull_ShouldRejectEachField()
    {
        var result = await _validator.ValidateAsync(new UpdateRoadmapRequestDto
        {
            Title = null,
            NumDays = null,
            OriginMode = null
        });

        result.Errors.Select(error => error.PropertyName).Should().Contain(["Title", "NumDays", "OriginMode"]);
    }

    // TEST-04: Reject every value immediately outside the RM-04 boundaries.
    /// <summary>Verifies partial fields retain the same limits as RM-01.</summary>
    [Fact]
    public async Task Validate_WithOutOfRangeSpecifiedFields_ShouldRejectEachField()
    {
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

        result.Errors.Select(error => error.PropertyName).Should().Contain([
            "Title", "NumDays", "BudgetAmount", "OriginMode", "OriginLatitude", "OriginLongitude", "OriginLabel"
        ]);
    }
}
