using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Covers every note boundary for reject menu decisions.</summary>
public sealed class RejectMenuSubmissionRequestDtoValidatorTests
{
    // TEST-01: Validate null, empty, whitespace, one, 1000 and 1001 character review notes.
    /// <summary>Verifies optionality, required content and the exact maximum length.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a", true)]
    [InlineData("1000", true)]
    [InlineData("1001", false)]
    public void Validate_NoteBoundaries(string? note, bool valid)
    {
        if (note is "1000" or "1001") note = new string('x', int.Parse(note));
        var result = new RejectMenuSubmissionRequestDtoValidator()
            .Validate(new RejectMenuSubmissionRequestDto { ReviewNote = note! });
        result.IsValid.Should().Be(valid);
        if (!valid) result.Errors.Should().ContainSingle(x => x.PropertyName == "ReviewNote");
    }
}
