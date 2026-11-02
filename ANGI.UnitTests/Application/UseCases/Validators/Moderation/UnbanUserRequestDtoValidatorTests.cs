using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Checks mandatory lift reason and length boundaries for MOD-U4.</summary>
public sealed class UnbanUserRequestDtoValidatorTests
{
    // TEST-01: Accept inclusive reason-length boundaries.
    /// <summary>Verifies valid lift reason with 1 and 1000 character boundaries.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(1000)]
    public void AcceptsValidBoundaries(int length)
    {
        var request = new UnbanUserRequestDto { LiftReason = new string('a', length) };
        new UnbanUserRequestDtoValidator().Validate(request).IsValid.Should().BeTrue();
    }

    // TEST-02: Reject null, empty, whitespace-only and overlong reasons.
    /// <summary>Checks missing-value and 1001-character reason boundaries.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("too-long")]
    public void RejectsInvalidReasons(string? reason)
    {
        var request = new UnbanUserRequestDto { LiftReason = reason == "too-long" ? new string('a', 1001) : reason! };
        new UnbanUserRequestDtoValidator().Validate(request).Errors.Should().ContainSingle(x => x.PropertyName == "LiftReason");
    }
}
