using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.Test.Application.UseCases.Validators.Restaurant;

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
