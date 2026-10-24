using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Roadmap;

public sealed class UpdateRoadmapItemRequestDtoValidatorTests
{
    private readonly UpdateRoadmapItemRequestDtoValidator _validator = new();

    // TEST-01: Accept an empty patch and every documented optional-field region.
    /// <summary>Verifies RM-08 permits omission and accepts positive ids plus all five meal slots.</summary>
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(1, 1L, "breakfast")]
    [InlineData(1, 1L, "lunch")]
    [InlineData(1, 1L, "dinner")]
    [InlineData(1, 1L, "snack")]
    [InlineData(1, 1L, "late_night")]
    public void Validate_WithDocumentedValues_ShouldSucceed(int? dishId, long? dayId, string? mealSlot)
    {
        var result = _validator.Validate(new UpdateRoadmapItemRequestDto
        {
            DishId = dishId,
            DayId = dayId,
            MealSlot = mealSlot,
            SortOrder = short.MinValue
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject every invalid id and meal-slot boundary before persistence.
    /// <summary>Verifies zero, negative, blank, case-variant, and unknown RM-08 values are rejected.</summary>
    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Validate_WithInvalidValue_ShouldFailExpectedProperty(
        UpdateRoadmapItemRequestDto request,
        string propertyName)
    {
        var result = _validator.Validate(request);

        result.Errors.Should().Contain(error => error.PropertyName == propertyName);
    }

    /// <summary>Provides every invalid RM-08 validation partition.</summary>
    public static TheoryData<UpdateRoadmapItemRequestDto, string> InvalidRequests() => new()
    {
        { new UpdateRoadmapItemRequestDto { DishId = 0 }, "DishId" },
        { new UpdateRoadmapItemRequestDto { DishId = -1 }, "DishId" },
        { new UpdateRoadmapItemRequestDto { DayId = 0 }, "DayId" },
        { new UpdateRoadmapItemRequestDto { DayId = -1 }, "DayId" },
        { new UpdateRoadmapItemRequestDto { MealSlot = "" }, "MealSlot" },
        { new UpdateRoadmapItemRequestDto { MealSlot = "Lunch" }, "MealSlot" },
        { new UpdateRoadmapItemRequestDto { MealSlot = "brunch" }, "MealSlot" }
    };
}
