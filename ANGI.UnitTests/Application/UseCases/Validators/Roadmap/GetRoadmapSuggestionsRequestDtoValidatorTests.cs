using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Roadmap;

public sealed class GetRoadmapSuggestionsRequestDtoValidatorTests
{
    private readonly GetRoadmapSuggestionsRequestDtoValidator _validator = new();

    // TEST-01: Accept the minimum request and every documented boundary value.
    /// <summary>Verifies RM-06 accepts no optional filters and inclusive coordinate, radius, and limit boundaries.</summary>
    [Fact]
    public void Validate_WithDocumentedBoundaryValues_ShouldPass()
    {
        _validator.Validate(new GetRoadmapSuggestionsRequestDto { Province = "Đà Nẵng" })
            .IsValid.Should().BeTrue();
        _validator.Validate(new GetRoadmapSuggestionsRequestDto
        {
            Province = "Đà Nẵng",
            DayId = 1,
            MealSlot = "late_night",
            TagIds = [1],
            ExcludeTagIds = [2],
            MaxPrice = 0,
            Lat = -90,
            Lng = 180,
            RadiusKm = 50,
            Limit = 50
        }).IsValid.Should().BeTrue();
    }

    // TEST-02: Reject each value outside the RM-06 contract on its own field.
    /// <summary>Verifies all numeric, enum, pair, required, and collection validation regions.</summary>
    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Validate_WithInvalidValue_ShouldFailOnThatField(
        GetRoadmapSuggestionsRequestDto request,
        string field)
    {
        _validator.Validate(request).Errors.Should().Contain(error => error.PropertyName == field);
    }

    /// <summary>Supplies one representative below, above, missing, or malformed value for every RM-06 rule.</summary>
    public static TheoryData<GetRoadmapSuggestionsRequestDto, string> InvalidRequests() => new()
    {
        { new GetRoadmapSuggestionsRequestDto { Province = "" }, "Province" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", DayId = 0 }, "DayId" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", MealSlot = "brunch" }, "MealSlot" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", MaxPrice = -1 }, "MaxPrice" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", Lat = 1 }, "Lng" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", Lng = 1 }, "Lat" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", Lat = -90.1m, Lng = 1 }, "Lat" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", Lat = 1, Lng = 180.1m }, "Lng" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", RadiusKm = 0 }, "RadiusKm" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", RadiusKm = 50.1m }, "RadiusKm" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", Limit = 0 }, "Limit" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", Limit = 51 }, "Limit" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", TagIds = [0] }, "TagIds[0]" },
        { new GetRoadmapSuggestionsRequestDto { Province = "x", ExcludeTagIds = [-1] }, "ExcludeTagIds[0]" }
    };
}
