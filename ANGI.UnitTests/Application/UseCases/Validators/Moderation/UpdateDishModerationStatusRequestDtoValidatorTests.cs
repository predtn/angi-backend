using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Checks required fields, enum partitions and reason boundaries.</summary>
public sealed class UpdateDishModerationStatusRequestDtoValidatorTests
{
    // TEST-01: Accept every documented status and the inclusive reason-length boundaries.
    /// <summary>Verifies valid enum strings with one and one thousand character reasons.</summary>
    [Theory]
    [InlineData("active", 1)]
    [InlineData("active", 1000)]
    [InlineData("hidden", 1)]
    [InlineData("hidden", 1000)]
    public void AcceptsValidBoundaries(string status, int length)
    {
        // STEP-01: Create request as UpdateDishModerationStatusRequestDto.
        var request = new UpdateDishModerationStatusRequestDto { Status = status, Reason = new string('a', length) };
        // STEP-02: Assert the validator.Validate().IsValid is true.
        new UpdateDishModerationStatusRequestDtoValidator().Validate(request).IsValid.Should().BeTrue();
    }

    // TEST-02: Reject missing, numeric, wrong-case and undocumented enum values.
    /// <summary>Verifies invalid status partitions report exactly one field error.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ACTIVE")]
    [InlineData("removed")]
    [InlineData("0")]
    [InlineData(null)]
    public void RejectsInvalidStatuses(string? status)
    {
        // STEP-01: Create request as UpdateDishModerationStatusRequestDto.
        var request = new UpdateDishModerationStatusRequestDto { Status = status!, Reason = "reason" };
        // STEP-02: Assert the validator.Validate().Errors has exactly one matching item.
        new UpdateDishModerationStatusRequestDtoValidator().Validate(request).Errors.Should().ContainSingle(x => x.PropertyName == "Status");
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
        // STEP-01: Create request as UpdateDishModerationStatusRequestDto.
        var request = new UpdateDishModerationStatusRequestDto { Status = "active", Reason = reason == "too-long" ? new string('a', 1001) : reason! };
        // STEP-02: Assert the validator.Validate().Errors has exactly one matching item.
        new UpdateDishModerationStatusRequestDtoValidator().Validate(request).Errors.Should().ContainSingle(x => x.PropertyName == "Reason");
    }
}
