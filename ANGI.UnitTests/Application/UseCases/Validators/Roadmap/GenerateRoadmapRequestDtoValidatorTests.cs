using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Roadmap;

public sealed class GenerateRoadmapRequestDtoValidatorTests
{
    private readonly GenerateRoadmapRequestDtoValidator _validator = new();

    // TEST-01: Accept the documented request of each scope, with every optional field at its limits.
    [Fact]
    public void Validate_WithDocumentedRequests_ShouldPass()
    {
        _validator.Validate(new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế" }).IsValid.Should().BeTrue();
        _validator.Validate(new GenerateRoadmapRequestDto
        {
            Scope = "day",
            DayId = 301,
            ReplaceExisting = false,
            Province = "Thành phố Đà Nẵng",
            MealSlots = ["breakfast", "lunch", "dinner", "snack", "late_night"],
            MaxPricePerMeal = 0,
            TagIds = [9],
            ExcludeTagIds = [12],
            Note = new string('a', 500),
            CurrentLatitude = -90,
            CurrentLongitude = 180,
            RadiusKm = 1
        }).IsValid.Should().BeTrue();
        _validator.Validate(new GenerateRoadmapRequestDto
        {
            Scope = "slot",
            DayId = 301,
            MealSlot = "late_night",
            Province = "Huế",
            RadiusKm = 50
        }).IsValid.Should().BeTrue();
    }

    // TEST-02: Reject each out-of-contract value on its own field.
    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Validate_WithInvalidValue_ShouldFailOnThatField(GenerateRoadmapRequestDto request, string field)
    {
        _validator.Validate(request).Errors.Should().Contain(error => error.PropertyName == field);
    }

    public static TheoryData<GenerateRoadmapRequestDto, string> InvalidRequests() => new()
    {
        { new GenerateRoadmapRequestDto { Scope = null!, Province = "Huế" }, "Scope" },
        { new GenerateRoadmapRequestDto { Scope = "week", Province = "Huế" }, "Scope" },
        { new GenerateRoadmapRequestDto { Scope = "day", Province = "Huế" }, "DayId" },
        { new GenerateRoadmapRequestDto { Scope = "slot", DayId = 1, Province = "Huế" }, "MealSlot" },
        { new GenerateRoadmapRequestDto { Scope = "full", DayId = 0, Province = "Huế" }, "DayId" },
        { new GenerateRoadmapRequestDto { Scope = "slot", DayId = 1, MealSlot = "brunch", Province = "Huế" }, "MealSlot" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = null! }, "Province" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = new string('a', 101) }, "Province" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", MealSlots = [] }, "MealSlots" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", MealSlots = ["lunch", "lunch"] }, "MealSlots" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", MealSlots = ["tea"] }, "MealSlots[0]" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", MaxPricePerMeal = -1 }, "MaxPricePerMeal" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", TagIds = [0] }, "TagIds[0]" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", ExcludeTagIds = [-1] }, "ExcludeTagIds[0]" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", Note = new string('a', 501) }, "Note" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", CurrentLatitude = 16 }, "CurrentLongitude" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", CurrentLongitude = 108 }, "CurrentLatitude" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", CurrentLatitude = 91, CurrentLongitude = 0 }, "CurrentLatitude" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", RadiusKm = 0.5m }, "RadiusKm" },
        { new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", RadiusKm = 51 }, "RadiusKm" }
    };
}
