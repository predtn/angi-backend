using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Validators.Discovery;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Discovery;

public sealed class BrowseDishesRequestDtoValidatorTests
{
    private readonly BrowseDishesRequestDtoValidator _validator = new();

    // TEST-01: Accept no filter at all and every documented filter at its limits.
    [Fact]
    public void Validate_WithDocumentedFilters_ShouldPass()
    {
        _validator.Validate(new BrowseDishesRequestDto()).IsValid.Should().BeTrue();
        _validator.Validate(new BrowseDishesRequestDto
        {
            Q = "bún",
            TagIds = [1],
            ExcludeTagIds = [2],
            DishForm = "other",
            MinPrice = 0,
            MaxPrice = 0,
            RestaurantId = 7,
            Lat = -90,
            Lng = 180,
            RadiusKm = 50,
            Limit = 50
        }).IsValid.Should().BeTrue();
    }

    // TEST-02: Reject each out-of-contract value on its own field.
    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Validate_WithInvalidValue_ShouldFailOnThatField(BrowseDishesRequestDto request, string field)
    {
        _validator.Validate(request).Errors.Should().Contain(error => error.PropertyName == field);
    }

    public static TheoryData<BrowseDishesRequestDto, string> InvalidRequests() => new()
    {
        { new BrowseDishesRequestDto { Limit = 0 }, "Limit" },
        { new BrowseDishesRequestDto { Limit = 51 }, "Limit" },
        { new BrowseDishesRequestDto { Lat = 10 }, "Lng" },
        { new BrowseDishesRequestDto { Lng = 10 }, "Lat" },
        { new BrowseDishesRequestDto { Lat = 91, Lng = 0 }, "Lat" },
        { new BrowseDishesRequestDto { RadiusKm = 5 }, "RadiusKm" },
        { new BrowseDishesRequestDto { Lat = 10, Lng = 10, RadiusKm = 50.1m }, "RadiusKm" },
        { new BrowseDishesRequestDto { Lat = 10, Lng = 10, RadiusKm = 0 }, "RadiusKm" },
        { new BrowseDishesRequestDto { MinPrice = -1 }, "MinPrice" },
        { new BrowseDishesRequestDto { MinPrice = 50000, MaxPrice = 40000 }, "MaxPrice" },
        { new BrowseDishesRequestDto { DishForm = "Soup" }, "DishForm" },
        { new BrowseDishesRequestDto { TagIds = [0] }, "TagIds[0]" },
        { new BrowseDishesRequestDto { ExcludeTagIds = [-1] }, "ExcludeTagIds[0]" },
        { new BrowseDishesRequestDto { RestaurantId = 0 }, "RestaurantId" }
    };
}
