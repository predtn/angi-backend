using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Validators.Discovery;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Discovery;

public sealed class RollDishRequestDtoValidatorTests
{
    private readonly RollDishRequestDtoValidator _validator = new();

    // TEST-01: Accept an empty body and every documented filter at its limits.
    [Fact]
    public void Validate_WithDocumentedFilters_ShouldPass()
    {
        _validator.Validate(new RollDishRequestDto()).IsValid.Should().BeTrue();
        _validator.Validate(new RollDishRequestDto
        {
            Province = "Thành phố Đà Nẵng",
            TagIds = [9],
            ExcludeTagIds = [12],
            MealTime = "snack",
            MaxPrice = 0,
            ExcludeDishIds = Enumerable.Range(1, 50).ToArray(),
            Latitude = -90,
            Longitude = 180,
            RadiusKm = 0.5m
        }).IsValid.Should().BeTrue();
        _validator.Validate(new RollDishRequestDto { Latitude = 90, Longitude = -180, RadiusKm = 50 }).IsValid.Should().BeTrue();
    }

    // TEST-02: Reject each out-of-contract value on its own field.
    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Validate_WithInvalidValue_ShouldFailOnThatField(RollDishRequestDto request, string field)
    {
        _validator.Validate(request).Errors.Should().Contain(error => error.PropertyName == field);
    }

    public static TheoryData<RollDishRequestDto, string> InvalidRequests() => new()
    {
        { new RollDishRequestDto { Latitude = 10 }, "Longitude" },
        { new RollDishRequestDto { Longitude = 10 }, "Latitude" },
        { new RollDishRequestDto { Latitude = 90.1m, Longitude = 0 }, "Latitude" },
        { new RollDishRequestDto { Latitude = 0, Longitude = -180.1m }, "Longitude" },
        { new RollDishRequestDto { RadiusKm = 3 }, "RadiusKm" },
        { new RollDishRequestDto { Latitude = 10, Longitude = 10, RadiusKm = 0.4m }, "RadiusKm" },
        { new RollDishRequestDto { Latitude = 10, Longitude = 10, RadiusKm = 50.1m }, "RadiusKm" },
        { new RollDishRequestDto { MealTime = "Lunch" }, "MealTime" },
        { new RollDishRequestDto { MealTime = "" }, "MealTime" },
        { new RollDishRequestDto { MaxPrice = -1 }, "MaxPrice" },
        { new RollDishRequestDto { ExcludeDishIds = Enumerable.Range(1, 51).ToArray() }, "ExcludeDishIds" },
        { new RollDishRequestDto { ExcludeDishIds = [0] }, "ExcludeDishIds[0]" },
        { new RollDishRequestDto { TagIds = [0] }, "TagIds[0]" },
        { new RollDishRequestDto { ExcludeTagIds = [-1] }, "ExcludeTagIds[0]" }
    };
}
