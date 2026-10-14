using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

public sealed class RegisterRestaurantRequestDtoValidatorTests
{
    // TEST-01: Reject overlapping opening intervals on the same day.
    /// <summary>Verifies that OWN-01 cannot persist ambiguous business-hour schedules.</summary>
    [Fact]
    public async Task ValidateAsync_WithOverlappingBusinessHours_ShouldBeInvalid()
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = new RegisterRestaurantRequestDto
        {
            Name = "Test Restaurant",
            Phone = "0123456789",
            AddressLine = "1 Test Street",
            District = "Test District",
            ProvinceName = "Test Province",
            Latitude = 10m,
            Longitude = 106m,
            BusinessHours =
            [
                new BusinessHourDto { DayOfWeek = 1, OpenTime = "08:00", CloseTime = "12:00" },
                new BusinessHourDto { DayOfWeek = 1, OpenTime = "11:00", CloseTime = "14:00" }
            ]
        };

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(request.BusinessHours));
    }

    // TEST-02: Reject an overnight interval that overlaps the following day's interval.
    [Fact]
    public async Task ValidateAsync_WithOvernightOverlapOnFollowingDay_ShouldBeInvalid()
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest(
            new BusinessHourDto { DayOfWeek = 1, OpenTime = "22:00", CloseTime = "02:00" },
            new BusinessHourDto { DayOfWeek = 2, OpenTime = "01:00", CloseTime = "03:00" });

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(request.BusinessHours));
    }

    // TEST-03: Allow separate early and overnight intervals that start on the same day.
    [Fact]
    public async Task ValidateAsync_WithNonOverlappingEarlyAndOvernightIntervals_ShouldBeValid()
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest(
            new BusinessHourDto { DayOfWeek = 1, OpenTime = "01:00", CloseTime = "03:00" },
            new BusinessHourDto { DayOfWeek = 1, OpenTime = "22:00", CloseTime = "02:00" });

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }

    // TEST-04: Detect overlaps that cross from Saturday into Sunday at the week boundary.
    [Fact]
    public async Task ValidateAsync_WithOverlapAcrossWeekBoundary_ShouldBeInvalid()
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest(
            new BusinessHourDto { DayOfWeek = 6, OpenTime = "22:00", CloseTime = "02:00" },
            new BusinessHourDto { DayOfWeek = 0, OpenTime = "01:00", CloseTime = "03:00" });

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
    }

    // TEST-05: Return Vietnamese messages, as for every other validator (coding_rule.md §8).
    [Fact]
    public async Task ValidateAsync_WithInvalidFields_ShouldReturnVietnameseMessages()
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest(
            new BusinessHourDto { DayOfWeek = 1, OpenTime = "8h", CloseTime = "12:00" });
        request.Name = "";
        request.Latitude = 95m;
        request.ImageMediaIds = [1, 1];

        var result = await validator.TestValidateAsync(request);

        result.ShouldHaveValidationErrorFor(x => x.Name).WithErrorMessage("Tên nhà hàng là bắt buộc.");
        result.ShouldHaveValidationErrorFor(x => x.Latitude)
            .WithErrorMessage("Vĩ độ phải nằm trong khoảng -90 đến 90.");
        result.ShouldHaveValidationErrorFor(x => x.ImageMediaIds)
            .WithErrorMessage("Danh sách ảnh không được trùng nhau.");
        result.ShouldHaveValidationErrorFor("BusinessHours[0].OpenTime")
            .WithErrorMessage("Giờ mở cửa phải có dạng HH:mm.");
    }

    // TEST-06: Enforce every required OWN-01 text field.
    [Theory]
    [InlineData("name")]
    [InlineData("phone")]
    [InlineData("addressLine")]
    [InlineData("district")]
    [InlineData("provinceName")]
    public async Task ValidateAsync_WithMissingRequiredText_ShouldBeInvalid(string field)
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest();
        switch (field)
        {
            case "name": request.Name = " "; break;
            case "phone": request.Phone = " "; break;
            case "addressLine": request.AddressLine = " "; break;
            case "district": request.District = " "; break;
            case "provinceName": request.ProvinceName = " "; break;
        }

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
    }

    // TEST-07: Exercise every documented restaurant text-length boundary.
    [Theory]
    [InlineData("name", 200, true)]
    [InlineData("name", 201, false)]
    [InlineData("description", 2000, true)]
    [InlineData("description", 2001, false)]
    [InlineData("phone", 20, true)]
    [InlineData("phone", 21, false)]
    [InlineData("addressLine", 255, true)]
    [InlineData("addressLine", 256, false)]
    [InlineData("ward", 100, true)]
    [InlineData("ward", 101, false)]
    [InlineData("district", 100, true)]
    [InlineData("district", 101, false)]
    [InlineData("provinceName", 100, true)]
    [InlineData("provinceName", 101, false)]
    public async Task ValidateAsync_WithTextLengthBoundary_ShouldMatchContract(
        string field,
        int length,
        bool expectedValid)
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest();
        var value = new string('a', length);
        switch (field)
        {
            case "name": request.Name = value; break;
            case "description": request.Description = value; break;
            case "phone": request.Phone = value; break;
            case "addressLine": request.AddressLine = value; break;
            case "ward": request.Ward = value; break;
            case "district": request.District = value; break;
            case "provinceName": request.ProvinceName = value; break;
        }

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-08: Accept normalized email and web URLs while rejecting malformed values and unsupported schemes.
    [Theory]
    [InlineData(" owner@example.com ", " https://example.com ", true)]
    [InlineData("owner.example.com", "https://example.com", false)]
    [InlineData("owner@example.com", "/relative", false)]
    [InlineData("owner@example.com", "ftp://example.com", false)]
    public async Task ValidateAsync_WithContactValues_ShouldMatchContract(
        string email,
        string website,
        bool expectedValid)
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest();
        request.Email = email;
        request.Website = website;

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-09: Accept coordinate endpoints and reject values immediately outside the geographic ranges.
    [Theory]
    [InlineData(-90, -180, true)]
    [InlineData(90, 180, true)]
    [InlineData(-90.01, 0, false)]
    [InlineData(90.01, 0, false)]
    [InlineData(0, -180.01, false)]
    [InlineData(0, 180.01, false)]
    public async Task ValidateAsync_WithCoordinateBoundary_ShouldMatchContract(
        double latitude,
        double longitude,
        bool expectedValid)
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest();
        request.Latitude = (decimal)latitude;
        request.Longitude = (decimal)longitude;

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-10: Require both coordinates rather than accepting nullable numeric defaults.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ValidateAsync_WithMissingCoordinate_ShouldBeInvalid(
        bool omitLatitude,
        bool omitLongitude)
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest();
        if (omitLatitude) request.Latitude = null;
        if (omitLongitude) request.Longitude = null;

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
    }

    // TEST-11: Exercise nullable price level and both sides of its 1-to-4 range.
    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    [InlineData(5, false)]
    public async Task ValidateAsync_WithPriceLevelBoundary_ShouldMatchContract(
        int? priceLevel,
        bool expectedValid)
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest();
        request.PriceLevel = (short?)priceLevel;

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-12: Enforce positive, unique media ids and the ten-image gallery maximum.
    [Fact]
    public async Task ValidateAsync_WithInvalidMediaIdentifiers_ShouldReportEachField()
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest();
        request.CoverMediaId = 0;
        request.ImageMediaIds = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, -1];

        var result = await validator.TestValidateAsync(request);

        result.ShouldHaveValidationErrorFor(item => item.CoverMediaId);
        result.ShouldHaveValidationErrorFor(item => item.ImageMediaIds);
        result.Errors.Count(error => error.PropertyName == nameof(request.ImageMediaIds)).Should().Be(2);
    }

    // TEST-13: Accept optional fields and the exact ten-image gallery boundary.
    [Fact]
    public async Task ValidateAsync_WithOptionalFieldsOmittedAndTenImages_ShouldBeValid()
    {
        var validator = new RegisterRestaurantRequestDtoValidator();
        var request = ValidRequest();
        request.Description = null;
        request.Email = null;
        request.Website = null;
        request.Ward = null;
        request.PriceLevel = null;
        request.CoverMediaId = null;
        request.ImageMediaIds = Enumerable.Range(1, 10).Select(id => (long)id).ToArray();
        request.BusinessHours = null;

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }

    private static RegisterRestaurantRequestDto ValidRequest(params BusinessHourDto[] businessHours) => new()
    {
        Name = "Test Restaurant",
        Phone = "0123456789",
        AddressLine = "1 Test Street",
        District = "Test District",
        ProvinceName = "Test Province",
        Latitude = 10m,
        Longitude = 106m,
        BusinessHours = businessHours
    };
}
