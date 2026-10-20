using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Roadmap;

public sealed class AddRoadmapItemRequestDtoValidatorTests
{
    private readonly AddRoadmapItemRequestDtoValidator _validator = new();

    // TEST-01: Accept every documented meal slot and both optional-field states.
    /// <summary>Verifies RM-07 valid enum values, smallint boundaries, and nullable reco id.</summary>
    [Theory]
    [InlineData("breakfast")]
    [InlineData("lunch")]
    [InlineData("dinner")]
    [InlineData("snack")]
    [InlineData("late_night")]
    public void Validate_WithDocumentedValues_ShouldPass(string mealSlot)
    {
        _validator.Validate(new AddRoadmapItemRequestDto
        {
            DishId = 1,
            MealSlot = mealSlot,
            SortOrder = short.MinValue,
            RecoRequestId = Guid.NewGuid()
        }).IsValid.Should().BeTrue();
        _validator.Validate(new AddRoadmapItemRequestDto
        {
            DishId = int.MaxValue,
            MealSlot = mealSlot,
            SortOrder = short.MaxValue
        }).IsValid.Should().BeTrue();
    }

    // TEST-02: Reject missing ids and missing or unknown meal slots once on the relevant field.
    /// <summary>Verifies every invalid RM-07 field region without duplicate missing-value errors.</summary>
    [Theory]
    [InlineData(0, "breakfast", "DishId")]
    [InlineData(-1, "breakfast", "DishId")]
    [InlineData(1, null, "MealSlot")]
    [InlineData(1, "", "MealSlot")]
    [InlineData(1, "Breakfast", "MealSlot")]
    public void Validate_WithInvalidValue_ShouldFailOnThatField(int dishId, string? mealSlot, string field)
    {
        var result = _validator.Validate(new AddRoadmapItemRequestDto
        {
            DishId = dishId,
            MealSlot = mealSlot!
        });

        result.Errors.Should().Contain(error => error.PropertyName == field);
        if (string.IsNullOrEmpty(mealSlot))
        {
            result.Errors.Count(error => error.PropertyName == "MealSlot").Should().Be(1);
        }
    }
}
