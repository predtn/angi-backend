using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Checks required fields, enum partitions and reason boundaries.</summary>
public sealed class UpdateRestaurantModerationStatusRequestDtoValidatorTests
{
    // TEST-01: Accept every documented status and the inclusive reason-length boundaries.
    /// <summary>Verifies valid enum strings with one and one thousand character reasons.</summary>
    [Theory]
    [InlineData("visible", 1)]
    [InlineData("visible", 1000)]
    [InlineData("hidden", 1)]
    [InlineData("hidden", 1000)]
    [InlineData("suspended", 1)]
    [InlineData("suspended", 1000)]
    public void AcceptsValidBoundaries(string status, int length)
    {
        var request = new UpdateRestaurantModerationStatusRequestDto { ModerationStatus = status, Reason = new string('a', length) };
        new UpdateRestaurantModerationStatusRequestDtoValidator().Validate(request).IsValid.Should().BeTrue();
    }

    // TEST-02: Reject missing, numeric, wrong-case and undocumented enum values.
    /// <summary>Verifies invalid status partitions report exactly one field error.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("VISIBLE")]
    [InlineData("removed")]
    [InlineData("0")]
    [InlineData(null)]
    public void RejectsInvalidStatuses(string? status)
    {
        var request = new UpdateRestaurantModerationStatusRequestDto { ModerationStatus = status!, Reason = "reason" };
        new UpdateRestaurantModerationStatusRequestDtoValidator().Validate(request).Errors.Should().ContainSingle(x => x.PropertyName == "ModerationStatus");
    }

    // TEST-03: Reject null, empty, whitespace-only and overlong explanations.
    /// <summary>Checks missing-value and one-thousand-and-one-character reason boundaries.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("too-long")]
    public void RejectsInvalidReasons(string? reason)
    {
        var request = new UpdateRestaurantModerationStatusRequestDto { ModerationStatus = "visible", Reason = reason == "too-long" ? new string('a', 1001) : reason! };
        new UpdateRestaurantModerationStatusRequestDtoValidator().Validate(request).Errors.Should().ContainSingle(x => x.PropertyName == "Reason");
    }
}
