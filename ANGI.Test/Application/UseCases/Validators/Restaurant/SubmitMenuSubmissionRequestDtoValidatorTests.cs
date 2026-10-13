using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.Test.Application.UseCases.Validators.Restaurant;

public sealed class SubmitMenuSubmissionRequestDtoValidatorTests
{
    private readonly SubmitMenuSubmissionRequestDtoValidator _validator = new();

    // TEST-01: Accept an omitted owner note.
    /// <summary>Verifies that ownerNote remains optional in OWN-17.</summary>
    [Fact]
    public async Task ValidateAsync_WithNullOwnerNote_ShouldBeValid()
    {
        var result = await _validator.ValidateAsync(new SubmitMenuSubmissionRequestDto());

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject an owner note longer than the documented 1000-character limit.
    /// <summary>Verifies field validation for an oversized OWN-17 ownerNote.</summary>
    [Fact]
    public async Task ValidateAsync_WithOwnerNoteOverMaximum_ShouldBeInvalid()
    {
        var result = await _validator.ValidateAsync(new SubmitMenuSubmissionRequestDto
        {
            OwnerNote = new string('a', 1001)
        });

        result.Errors.Should().ContainSingle(error => error.PropertyName == "OwnerNote");
    }

    // TEST-03: Accept an owner note at the exact 1000 UTF-16 code-unit boundary.
    /// <summary>Verifies the inclusive documented maximum.</summary>
    [Fact]
    public async Task ValidateAsync_WithExactlyMaximumOwnerNote_ShouldBeValid()
    {
        var result = await _validator.ValidateAsync(new SubmitMenuSubmissionRequestDto
        {
            OwnerNote = new string('a', 1000)
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-04: Accept empty, whitespace-only, and padded optional owner notes.
    /// <summary>Verifies that optional text is normalized by the use case rather than rejected.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \t\r\n")]
    [InlineData("  Gửi duyệt  ")]
    public async Task ValidateAsync_WithOptionalTextForms_ShouldBeValid(string ownerNote)
    {
        var result = await _validator.ValidateAsync(new SubmitMenuSubmissionRequestDto
        {
            OwnerNote = ownerNote
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-05: Accept 500 emoji because they occupy exactly 1000 UTF-16 code units.
    /// <summary>Documents the validator's UTF-16 length semantics for supplementary characters.</summary>
    [Fact]
    public async Task ValidateAsync_WithFiveHundredEmoji_ShouldBeValid()
    {
        var result = await _validator.ValidateAsync(new SubmitMenuSubmissionRequestDto
        {
            OwnerNote = string.Concat(Enumerable.Repeat("😀", 500))
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-06: Reject 501 emoji because they occupy 1002 UTF-16 code units.
    /// <summary>Locks down the current code-unit interpretation of the 1000-character contract.</summary>
    [Fact]
    public async Task ValidateAsync_WithFiveHundredOneEmoji_ShouldBeInvalid()
    {
        var result = await _validator.ValidateAsync(new SubmitMenuSubmissionRequestDto
        {
            OwnerNote = string.Concat(Enumerable.Repeat("😀", 501))
        });

        result.Errors.Should().ContainSingle(error => error.PropertyName == "OwnerNote");
    }
}
