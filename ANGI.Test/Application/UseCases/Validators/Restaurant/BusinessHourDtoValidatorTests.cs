using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.Test.Application.UseCases.Validators.Restaurant;

public sealed class BusinessHourDtoValidatorTests
{
    // TEST-01: Accept every day boundary and both ordinary and overnight opening intervals.
    [Theory]
    [InlineData(0, "00:00", "23:59")]
    [InlineData(6, "22:00", "02:00")]
    public async Task ValidateAsync_WithValidInterval_ShouldBeValid(
        short dayOfWeek,
        string openTime,
        string closeTime)
    {
        var validator = new BusinessHourDtoValidator();

        var result = await validator.ValidateAsync(new BusinessHourDto
        {
            DayOfWeek = dayOfWeek,
            OpenTime = openTime,
            CloseTime = closeTime
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject day values immediately outside the Sunday-to-Saturday range.
    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public async Task ValidateAsync_WithInvalidDay_ShouldBeInvalid(short dayOfWeek)
    {
        var validator = new BusinessHourDtoValidator();

        var result = await validator.ValidateAsync(new BusinessHourDto
        {
            DayOfWeek = dayOfWeek,
            OpenTime = "08:00",
            CloseTime = "17:00"
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(BusinessHourDto.DayOfWeek));
    }

    // TEST-03: Require exact zero-padded 24-hour HH:mm values for both endpoints.
    [Theory]
    [InlineData("8:00", "17:00")]
    [InlineData("08:00", "17:0")]
    [InlineData("24:00", "17:00")]
    [InlineData("08:00", "12:60")]
    [InlineData("", "17:00")]
    [InlineData("08:00", "")]
    public async Task ValidateAsync_WithInvalidTimeFormat_ShouldBeInvalid(
        string openTime,
        string closeTime)
    {
        var validator = new BusinessHourDtoValidator();

        var result = await validator.ValidateAsync(new BusinessHourDto
        {
            DayOfWeek = 1,
            OpenTime = openTime,
            CloseTime = closeTime
        });

        result.IsValid.Should().BeFalse();
    }

    // TEST-04: Reject a zero-duration interval whose opening and closing values are identical.
    [Fact]
    public async Task ValidateAsync_WithEqualOpeningAndClosingTimes_ShouldBeInvalid()
    {
        var validator = new BusinessHourDtoValidator();

        var result = await validator.ValidateAsync(new BusinessHourDto
        {
            DayOfWeek = 1,
            OpenTime = "08:00",
            CloseTime = "08:00"
        });

        result.IsValid.Should().BeFalse();
    }
}
