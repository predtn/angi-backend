using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.UnitTests.Application.UseCases.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Checks MOD-RP4 mandatory enums, optional text and conditional deadlines.</summary>
public sealed class ResolveReportRequestDtoValidatorTests
{
    // TEST-01: Accept every documented action for either documented outcome before target-specific checks.
    /// <summary>Covers the complete request enum domain and the related-report default.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("hide_blog")]
    [InlineData("remove_blog")]
    [InlineData("warn_user")]
    [InlineData("suspend_user")]
    [InlineData("ban_user")]
    [InlineData("hide_restaurant")]
    [InlineData("suspend_restaurant")]
    public void AcceptsEveryAction(string action)
    {
        var world = new ModerationTestContext();
        foreach (var outcome in new[] { "resolved", "dismissed" })
        {
            var request = new ResolveReportRequestDto { Outcome = outcome, ResolutionAction = action,
                SuspendUntil = world.Clock.GetUtcNow().AddSeconds(1) };
            new ResolveReportRequestDtoValidator(world.Clock).Validate(request).IsValid.Should().BeTrue();
            request.ApplyToRelatedReports.Should().BeTrue();
        }
    }

    // TEST-02: Reject null, empty, whitespace, wrong-case, numeric and unsupported enum strings once per field.
    /// <summary>Checks both required request enum properties independently.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("RESOLVED")]
    [InlineData("0")]
    [InlineData("invalid")]
    public void RejectsInvalidEnums(string? value)
    {
        var validator = new ResolveReportRequestDtoValidator(new ModerationTestContext().Clock);
        var result = validator.Validate(new ResolveReportRequestDto { Outcome = value!, ResolutionAction = value! });
        result.Errors.Should().ContainSingle(x => x.PropertyName == "Outcome");
        result.Errors.Should().ContainSingle(x => x.PropertyName == "ResolutionAction");
    }

    // TEST-03: Suspension requires a deadline strictly after now; other actions do not require one.
    /// <summary>Checks absent, past, exact-now and future deadline boundaries.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ValidatesSuspensionDeadline(int? seconds, bool valid)
    {
        var world = new ModerationTestContext();
        var request = new ResolveReportRequestDto { Outcome = "resolved", ResolutionAction = "suspend_user",
            SuspendUntil = seconds.HasValue ? world.Clock.GetUtcNow().AddSeconds(seconds.Value) : null };
        new ResolveReportRequestDtoValidator(world.Clock).Validate(request).IsValid.Should().Be(valid);
    }

    // TEST-04: Optional resolution notes allow null and zero through one thousand characters, rejecting overflow.
    /// <summary>Checks all optional-note length boundaries.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void ValidatesNoteLength(int? length, bool valid)
    {
        var request = new ResolveReportRequestDto { Outcome = "resolved", ResolutionAction = "none",
            ResolutionNote = length.HasValue ? new string('a', length.Value) : null };
        new ResolveReportRequestDtoValidator(new ModerationTestContext().Clock).Validate(request).IsValid.Should().Be(valid);
    }
}
