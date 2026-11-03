using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Verifies UpdateBusinessHoursRequestDtoValidator checks each interval and leaves overlap detection to the use case.</summary>
public sealed class UpdateBusinessHoursRequestDtoValidatorTests
{
    private readonly UpdateBusinessHoursRequestDtoValidator _validator = new();

    /// <summary>Creates a requested business hour interval.</summary>
    private static BusinessHourDto Hour(short day, string open, string close) =>
        new() { DayOfWeek = day, OpenTime = open, CloseTime = close };

    // TEST-01: Accept an empty schedule and the documented sample, including overnight and several intervals a day.
    /// <summary>Ensures empty and documented schedules are accepted.</summary>
    [Fact]
    public void Validate_WithDocumentedSchedules_ShouldPass()
    {
        _validator.Validate(new UpdateBusinessHoursRequestDto { Hours = [] }).IsValid.Should().BeTrue();
        _validator.Validate(new UpdateBusinessHoursRequestDto
        {
            Hours = [Hour(1, "06:00", "13:30"), Hour(1, "17:00", "02:00"), Hour(0, "00:00", "23:59")]
        }).IsValid.Should().BeTrue();
    }

    // TEST-02: Reject a missing hours array: an absent field is not "no hours declared".
    /// <summary>Ensures a missing array is not mistaken for an empty schedule.</summary>
    [Fact]
    public void Validate_WithoutHours_ShouldFailOnHours()
    {
        var result = _validator.Validate(new UpdateBusinessHoursRequestDto { Hours = null });

        result.Errors.Should().ContainSingle(e => e.PropertyName == "Hours");
    }

    // TEST-03: Reject a day outside 0..6, a time not in HH:mm, and an interval that opens and closes at once.
    /// <summary>Ensures a malformed interval is rejected as a field error.</summary>
    [Theory]
    [InlineData((short)7, "06:00", "13:30")]
    [InlineData((short)-1, "06:00", "13:30")]
    [InlineData((short)1, "6:00", "13:30")]
    [InlineData((short)1, "06:00", "24:00")]
    [InlineData((short)1, "08:00", "08:00")]
    public void Validate_WithInvalidInterval_ShouldFail(short day, string open, string close)
    {
        var result = _validator.Validate(new UpdateBusinessHoursRequestDto { Hours = [Hour(day, open, close)] });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().OnlyContain(e => e.PropertyName.StartsWith("Hours[0]"));
    }

    // TEST-04: Leave overlaps to the use case, which reports INVALID_BUSINESS_HOURS.
    /// <summary>Ensures overlaps reach the use case so it can return INVALID_BUSINESS_HOURS.</summary>
    [Fact]
    public void Validate_WithOverlappingIntervals_ShouldPass()
    {
        var request = new UpdateBusinessHoursRequestDto { Hours = [Hour(1, "06:00", "13:30"), Hour(1, "13:00", "20:00")] };

        _validator.Validate(request).IsValid.Should().BeTrue();
    }
}
