using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Verifies BrowseRestaurantsRequestDtoValidator enforces the REST-01 filter, sort and paging rules.</summary>
public sealed class BrowseRestaurantsRequestDtoValidatorTests
{
    private readonly BrowseRestaurantsRequestDtoValidator _validator = new();

    // TEST-01: Valid default request passes validation
    /// <summary>Ensures a request with only defaults is accepted.</summary>
    [Fact]
    public void Validate_WithValidDefaultRequest_ShouldBeValid()
    {
        var request = new BrowseRestaurantsRequestDto
        {
            Page = 1,
            PageSize = 20
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Sort=distance without Lat/Lng fails validation (matching REST-01 spec)
    /// <summary>Ensures distance sorting requires a location.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(10.5, null)]
    [InlineData(null, 106.5)]
    public void Validate_WithSortDistanceAndMissingCoordinates_ShouldHaveValidationError(double? lat, double? lng)
    {
        var request = new BrowseRestaurantsRequestDto
        {
            Sort = "distance",
            Lat = lat.HasValue ? (decimal)lat.Value : null,
            Lng = lng.HasValue ? (decimal)lng.Value : null
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeFalse();
        if (!lat.HasValue)
        {
            result.ShouldHaveValidationErrorFor(x => x.Lat)
                .WithErrorMessage("Thiếu tọa độ lat khi sắp xếp theo khoảng cách.");
        }
        if (!lng.HasValue)
        {
            result.ShouldHaveValidationErrorFor(x => x.Lng)
                .WithErrorMessage("Thiếu tọa độ lng khi sắp xếp theo khoảng cách.");
        }
    }

    // TEST-03: RadiusKm without Lat/Lng fails validation
    /// <summary>Ensures a radius filter requires a location.</summary>
    [Fact]
    public void Validate_WithRadiusKmMissingCoordinates_ShouldHaveValidationError()
    {
        var request = new BrowseRestaurantsRequestDto
        {
            RadiusKm = 10,
            Lat = null,
            Lng = null
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeFalse();
        result.ShouldHaveValidationErrorFor(x => x.Lat);
        result.ShouldHaveValidationErrorFor(x => x.Lng);
    }

    // TEST-04: RadiusKm exceeding 50 or non-positive fails validation
    /// <summary>Ensures the radius stays positive and within 50 km.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(51)]
    [InlineData(100)]
    public void Validate_WithInvalidRadiusKm_ShouldHaveValidationError(decimal radiusKm)
    {
        var request = new BrowseRestaurantsRequestDto
        {
            Lat = 10.5m,
            Lng = 106.5m,
            RadiusKm = radiusKm
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeFalse();
        result.ShouldHaveValidationErrorFor(x => x.RadiusKm);
    }

    // TEST-05: PriceLevel out of range 1..4 fails validation
    /// <summary>Ensures the price level stays within 1..4.</summary>
    [Theory]
    [InlineData((short)0)]
    [InlineData((short)5)]
    public void Validate_WithInvalidPriceLevel_ShouldHaveValidationError(short priceLevel)
    {
        var request = new BrowseRestaurantsRequestDto
        {
            PriceLevel = priceLevel
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeFalse();
        result.ShouldHaveValidationErrorFor(x => x.PriceLevel);
    }

    // TEST-06: MinRating out of range 1..5 fails validation
    /// <summary>Ensures the minimum rating stays within 1..5.</summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(5.5)]
    public void Validate_WithInvalidMinRating_ShouldHaveValidationError(double minRating)
    {
        var request = new BrowseRestaurantsRequestDto
        {
            MinRating = (decimal)minRating
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeFalse();
        result.ShouldHaveValidationErrorFor(x => x.MinRating);
    }

    // TEST-07: Pagination parameters out of valid bounds fail validation
    /// <summary>Ensures page and page size stay within their ranges.</summary>
    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void Validate_WithInvalidPagination_ShouldHaveValidationError(int page, int pageSize)
    {
        var request = new BrowseRestaurantsRequestDto
        {
            Page = page,
            PageSize = pageSize
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeFalse();
    }

    // TEST-08: Invalid sort enum value fails validation
    /// <summary>Ensures only documented sort values are accepted.</summary>
    [Fact]
    public void Validate_WithInvalidSort_ShouldHaveValidationError()
    {
        var request = new BrowseRestaurantsRequestDto
        {
            Sort = "invalid_sort_value"
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeFalse();
        result.ShouldHaveValidationErrorFor(x => x.Sort);
    }
}
