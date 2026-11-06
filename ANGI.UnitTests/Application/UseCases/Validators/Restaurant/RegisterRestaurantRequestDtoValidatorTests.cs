using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Verifies RegisterRestaurantRequestDtoValidator checks restaurant fields, media and overlapping business hours.</summary>
public sealed class RegisterRestaurantRequestDtoValidatorTests
{
    // TEST-01: Reject overlapping opening intervals on the same day.
    /// <summary>Verifies that OWN-01 cannot persist ambiguous business-hour schedules.</summary>
    [Fact]
    public async Task ValidateAsync_WithOverlappingBusinessHours_ShouldBeInvalid()
    {
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request as RegisterRestaurantRequestDto.
        var request = new RegisterRestaurantRequestDto
        {
            Name = "Test Restaurant",
            Phone = "0901234567",
            AddressLine = "1 Test Street",
            ProvinceName = "Test Province",
            Latitude = 10m,
            Longitude = 106m,
            BusinessHours =
            [
                new BusinessHourDto { DayOfWeek = 1, OpenTime = "08:00", CloseTime = "12:00" },
                new BusinessHourDto { DayOfWeek = 1, OpenTime = "11:00", CloseTime = "14:00" }
            ]
        };

        // STEP-03: Validate request and keep result.
        var result = await validator.ValidateAsync(request);

        // STEP-04: Assert result.IsValid is false and result.Errors contains a matching item.
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(request.BusinessHours));
    }

    // TEST-02: Reject an overnight interval that overlaps the following day's interval.
    /// <summary>Reject an overnight interval that overlaps the following day&apos;s interval.</summary>
    [Fact]
    public async Task ValidateAsync_WithOvernightOverlapOnFollowingDay_ShouldBeInvalid()
    {
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest.
        var request = ValidRequest(
            new BusinessHourDto { DayOfWeek = 1, OpenTime = "22:00", CloseTime = "02:00" },
            new BusinessHourDto { DayOfWeek = 2, OpenTime = "01:00", CloseTime = "03:00" });

        // STEP-03: Validate request and keep result.
        var result = await validator.ValidateAsync(request);

        // STEP-04: Assert result.IsValid is false and result.Errors contains a matching item.
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(request.BusinessHours));
    }

    // TEST-03: Allow separate early and overnight intervals that start on the same day.
    /// <summary>Allow separate early and overnight intervals that start on the same day.</summary>
    [Fact]
    public async Task ValidateAsync_WithNonOverlappingEarlyAndOvernightIntervals_ShouldBeValid()
    {
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest.
        var request = ValidRequest(
            new BusinessHourDto { DayOfWeek = 1, OpenTime = "01:00", CloseTime = "03:00" },
            new BusinessHourDto { DayOfWeek = 1, OpenTime = "22:00", CloseTime = "02:00" });

        // STEP-03: Validate request and keep result.
        var result = await validator.ValidateAsync(request);

        // STEP-04: Assert result.IsValid is true.
        result.IsValid.Should().BeTrue();
    }

    // TEST-04: Detect overlaps that cross from Saturday into Sunday at the week boundary.
    /// <summary>Detect overlaps that cross from Saturday into Sunday at the week boundary.</summary>
    [Fact]
    public async Task ValidateAsync_WithOverlapAcrossWeekBoundary_ShouldBeInvalid()
    {
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest.
        var request = ValidRequest(
            new BusinessHourDto { DayOfWeek = 6, OpenTime = "22:00", CloseTime = "02:00" },
            new BusinessHourDto { DayOfWeek = 0, OpenTime = "01:00", CloseTime = "03:00" });

        // STEP-03: Validate request and keep result.
        var result = await validator.ValidateAsync(request);

        // STEP-04: Assert result.IsValid is false.
        result.IsValid.Should().BeFalse();
    }

    // TEST-05: Return Vietnamese messages, as for every other validator (coding_rule.md §8).
    /// <summary>Return Vietnamese messages, as for every other validator (coding_rule.md §8).</summary>
    [Fact]
    public async Task ValidateAsync_WithInvalidFields_ShouldReturnVietnameseMessages()
    {
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest, set request.Name to an empty string, set request.Latitude to 95m and set request.ImageMediaIds to a test items.
        var request = ValidRequest(
            new BusinessHourDto { DayOfWeek = 1, OpenTime = "8h", CloseTime = "12:00" });
        request.Name = "";
        request.Latitude = 95m;
        request.ImageMediaIds = [1, 1];

        // STEP-03: Validate request and keep result.
        var result = await validator.TestValidateAsync(request);

        // STEP-04: Assert a validation error on Name with the expected message, a validation error on Latitude with the expected message and 2 more.
        result.ShouldHaveValidationErrorFor(x => x.Name).WithErrorMessage("Tên nhà hàng là bắt buộc.");
        result.ShouldHaveValidationErrorFor(x => x.Latitude)
            .WithErrorMessage("Vĩ độ phải nằm trong khoảng -90 đến 90.");
        result.ShouldHaveValidationErrorFor(x => x.ImageMediaIds)
            .WithErrorMessage("Danh sách ảnh không được trùng nhau.");
        result.ShouldHaveValidationErrorFor("BusinessHours[0].OpenTime")
            .WithErrorMessage("Giờ mở cửa phải có dạng HH:mm.");
    }

    // TEST-06: Enforce every required OWN-01 text field.
    /// <summary>Enforce every required OWN-01 text field.</summary>
    [Theory]
    [InlineData("name")]
    [InlineData("phone")]
    [InlineData("addressLine")]
    [InlineData("provinceName")]
    public async Task ValidateAsync_WithMissingRequiredText_ShouldBeInvalid(string field)
    {
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest and apply the case selected by field.
        var request = ValidRequest();
        switch (field)
        {
            case "name": request.Name = " "; break;
            case "phone": request.Phone = " "; break;
            case "addressLine": request.AddressLine = " "; break;
            case "provinceName": request.ProvinceName = " "; break;
        }

        // STEP-03: Validate request and keep result.
        var result = await validator.ValidateAsync(request);

        // STEP-04: Assert result.IsValid is false.
        result.IsValid.Should().BeFalse();
    }

    // TEST-07: Exercise every documented restaurant text-length boundary.
    /// <summary>Exercise every documented restaurant text-length boundary.</summary>
    [Theory]
    [InlineData("name", 200, true)]
    [InlineData("name", 201, false)]
    [InlineData("description", 2000, true)]
    [InlineData("description", 2001, false)]
    [InlineData("phone", 20, false)]
    [InlineData("phone", 21, false)]
    [InlineData("addressLine", 255, true)]
    [InlineData("addressLine", 256, false)]
    [InlineData("ward", 100, true)]
    [InlineData("ward", 101, false)]
    [InlineData("provinceName", 100, true)]
    [InlineData("provinceName", 101, false)]
    public async Task ValidateAsync_WithTextLengthBoundary_ShouldMatchContract(
        string field,
        int length,
        bool expectedValid)
    {
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest, create value as string and apply the case selected by field.
        var request = ValidRequest();
        var value = new string('a', length);
        switch (field)
        {
            case "name": request.Name = value; break;
            case "description": request.Description = value; break;
            case "phone": request.Phone = value; break;
            case "addressLine": request.AddressLine = value; break;
            case "ward": request.Ward = value; break;
            case "provinceName": request.ProvinceName = value; break;
        }

        // STEP-03: Validate request and keep result.
        var result = await validator.ValidateAsync(request);

        // STEP-04: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-08: Accept normalized email and web URLs while rejecting malformed values and unsupported schemes.
    /// <summary>Accept normalized email and web URLs while rejecting malformed values and unsupported schemes.</summary>
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
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest, set request.Email to email and set request.Website to website.
        var request = ValidRequest();
        request.Email = email;
        request.Website = website;

        // STEP-03: Validate request and keep result.
        var result = await validator.ValidateAsync(request);

        // STEP-04: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-09: Accept coordinate endpoints and reject values immediately outside the geographic ranges.
    /// <summary>Accept coordinate endpoints and reject values immediately outside the geographic ranges.</summary>
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
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest, set request.Latitude to latitude and set request.Longitude to longitude.
        var request = ValidRequest();
        request.Latitude = (decimal)latitude;
        request.Longitude = (decimal)longitude;

        // STEP-03: Validate request and keep result.
        var result = await validator.ValidateAsync(request);

        // STEP-04: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-10: Require both coordinates rather than accepting nullable numeric defaults.
    /// <summary>Require both coordinates rather than accepting nullable numeric defaults.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ValidateAsync_WithMissingCoordinate_ShouldBeInvalid(
        bool omitLatitude,
        bool omitLongitude)
    {
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest, when omitLatitude, set request.Latitude to null and when omitLongitude, set request.Longitude to null.
        var request = ValidRequest();
        if (omitLatitude) request.Latitude = null;
        if (omitLongitude) request.Longitude = null;

        // STEP-03: Validate request and keep result.
        var result = await validator.ValidateAsync(request);

        // STEP-04: Assert result.IsValid is false.
        result.IsValid.Should().BeFalse();
    }

    // TEST-11: Exercise nullable price level and both sides of its 1-to-4 range.
    /// <summary>Exercise nullable price level and both sides of its 1-to-4 range.</summary>
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
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest and set request.PriceLevel to priceLevel.
        var request = ValidRequest();
        request.PriceLevel = (short?)priceLevel;

        // STEP-03: Validate request and keep result.
        var result = await validator.ValidateAsync(request);

        // STEP-04: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-12: Enforce positive, unique media ids and the ten-image gallery maximum.
    /// <summary>Enforce positive, unique media ids and the ten-image gallery maximum.</summary>
    [Fact]
    public async Task ValidateAsync_WithInvalidMediaIdentifiers_ShouldReportEachField()
    {
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest, set request.CoverMediaId to 0 and set request.ImageMediaIds to a test items.
        var request = ValidRequest();
        request.CoverMediaId = 0;
        request.ImageMediaIds = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, -1];

        // STEP-03: Validate request and keep result.
        var result = await validator.TestValidateAsync(request);

        // STEP-04: Assert a validation error on CoverMediaId, a validation error on ImageMediaIds and result.Errors.Count(filtered) is 2.
        result.ShouldHaveValidationErrorFor(item => item.CoverMediaId);
        result.ShouldHaveValidationErrorFor(item => item.ImageMediaIds);
        result.Errors.Count(error => error.PropertyName == nameof(request.ImageMediaIds)).Should().Be(2);
    }

    // TEST-13: Accept optional fields and the exact ten-image gallery boundary.
    /// <summary>Accept optional fields and the exact ten-image gallery boundary.</summary>
    [Fact]
    public async Task ValidateAsync_WithOptionalFieldsOmittedAndTenImages_ShouldBeValid()
    {
        // STEP-01: Create the RegisterRestaurantRequestDtoValidator under test as validator.
        var validator = new RegisterRestaurantRequestDtoValidator();
        // STEP-02: Create request through ValidRequest, set request.Description to null, set request.Email to null, set request.Website to null and 5 more.
        var request = ValidRequest();
        request.Description = null;
        request.Email = null;
        request.Website = null;
        request.Ward = null;
        request.PriceLevel = null;
        request.CoverMediaId = null;
        request.ImageMediaIds = Enumerable.Range(1, 10).Select(id => (long)id).ToArray();
        request.BusinessHours = null;

        // STEP-03: Validate request and keep result.
        var result = await validator.ValidateAsync(request);

        // STEP-04: Assert result.IsValid is true.
        result.IsValid.Should().BeTrue();
    }

    // TEST-14: Validate Vietnamese phone formats and return exactly one error for invalid or missing phone values.
    /// <summary>Verifies registration enforces contact number rules before any restaurant can be created.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("1234567890", false)]
    [InlineData("0123456789", false)]
    [InlineData("090123456", false)]
    [InlineData("09012345678", false)]
    [InlineData("0901234567\n", false)]
    [InlineData("+840901234567", false)]
    [InlineData("+6590123456", false)]
    [InlineData("0901234567", true)]
    [InlineData("0236 3812 345", true)]
    [InlineData("+84 90 123 4567", true)]
    public async Task ValidateAsync_WithVietnamesePhone_ShouldReportOnlyThePhoneError(string? phone, bool expectedValid)
    {
        // STEP-01: Create request through ValidRequest and set request.Phone to phone.
        var request = ValidRequest();
        request.Phone = phone!;

        // STEP-02: Validate request and keep result.
        var result = await new RegisterRestaurantRequestDtoValidator().ValidateAsync(request);

        // STEP-03: Assert result.IsValid is expectedValid, result.Errors has exactly one matching item, result.Errors.PropertyName is nameof() and 1 more.
        result.IsValid.Should().Be(expectedValid);
        if (!expectedValid)
        {
            result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be(nameof(request.Phone));
            result.Errors[0].ErrorMessage.Should().Be(string.IsNullOrWhiteSpace(phone)
                ? "Số điện thoại là bắt buộc."
                : "Số điện thoại phải là số di động hoặc số cố định Việt Nam hợp lệ.");
        }
    }

    // TEST-15: Enforce the raw 20-character phone limit after requiring a valid number format.
    /// <summary>Verifies an otherwise valid number at the maximum length passes and one extra character fails once.</summary>
    [Theory]
    [InlineData(19, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public async Task ValidateAsync_WithFormattedPhoneLength_ShouldMatchLimit(int length, bool expectedValid)
    {
        // STEP-01: Create request through ValidRequest and set request.Phone to "0901234567".PadRight().
        var request = ValidRequest();
        request.Phone = "0901234567".PadRight(length);

        // STEP-02: Validate request and keep result.
        var result = await new RegisterRestaurantRequestDtoValidator().ValidateAsync(request);

        // STEP-03: Assert result.IsValid is expectedValid, result.Errors has exactly one matching item and result.Errors.ErrorMessage is the expected text.
        result.IsValid.Should().Be(expectedValid);
        if (!expectedValid)
        {
            result.Errors.Should().ContainSingle().Which.ErrorMessage
                .Should().Be("Số điện thoại không được vượt quá 20 ký tự.");
        }
    }

    // TEST-16: Reject null elements inside an otherwise optional business-hour list.
    /// <summary>Prevents a JSON null interval from reaching aggregate mapping and causing an internal server error.</summary>
    [Fact]
    public async Task ValidateAsync_WithNullBusinessHour_ShouldReportIndexedField()
    {
        // STEP-01: Create request through ValidRequest and set request.BusinessHours to a test items.
        var request = ValidRequest();
        request.BusinessHours = [null!];

        // STEP-02: Validate request and keep result.
        var result = await new RegisterRestaurantRequestDtoValidator().ValidateAsync(request);

        // STEP-03: Assert result.Errors has exactly one matching item and result.Errors.PropertyName is "BusinessHours[0]".
        result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be("BusinessHours[0]");
    }

    /// <summary>Creates a valid registration baseline so each test isolates the intended boundary.</summary>
    private static RegisterRestaurantRequestDto ValidRequest(params BusinessHourDto[] businessHours) => new()
    {
        Name = "Test Restaurant",
        Phone = "0901234567",
        AddressLine = "1 Test Street",
        ProvinceName = "Test Province",
        Latitude = 10m,
        Longitude = 106m,
        BusinessHours = businessHours
    };
}
