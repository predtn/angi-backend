using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

public sealed class ReviewVerificationRequestDtoValidatorTests
{
    private readonly ReviewVerificationRequestDtoValidator _validator = new();

    // TEST-01: Accept approve without a note and non-approve decisions with a note.
    /// <summary>Verifies all documented valid MOD-V3 decision branches.</summary>
    [Theory]
    [InlineData("approve", null)]
    [InlineData("reject", "Giấy phép không hợp lệ.")]
    [InlineData("needs_more_info", "Vui lòng bổ sung giấy tờ.")]
    public void Validate_WithValidDecision_ShouldSucceed(string decision, string? note)
    {
        var result = _validator.Validate(new ReviewVerificationRequestDto
        {
            Decision = decision,
            ReviewNote = note
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject null, empty, and unknown decisions with one decision error.
    /// <summary>Verifies the required decision and enum boundary without duplicate missing-value errors.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cancel")]
    public void Validate_WithInvalidDecision_ShouldFail(string? decision)
    {
        var result = _validator.Validate(new ReviewVerificationRequestDto { Decision = decision! });

        result.Errors.Count(error => error.PropertyName == nameof(ReviewVerificationRequestDto.Decision)).Should().Be(1);
    }

    // TEST-03: Require a non-whitespace note for reject and needs_more_info.
    /// <summary>Verifies both conditional review-note branches.</summary>
    [Theory]
    [InlineData("reject", null)]
    [InlineData("reject", "  ")]
    [InlineData("needs_more_info", "")]
    public void Validate_WithoutRequiredReviewNote_ShouldFail(string decision, string? note)
    {
        var result = _validator.Validate(new ReviewVerificationRequestDto
        {
            Decision = decision,
            ReviewNote = note
        });

        result.Errors.Should().Contain(error => error.PropertyName == nameof(ReviewVerificationRequestDto.ReviewNote));
    }

    // TEST-04: Accept exactly one thousand characters and reject one thousand and one.
    /// <summary>Verifies the inclusive maximum review-note length.</summary>
    [Theory]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void Validate_AtReviewNoteLengthBoundary_ShouldMatchContract(int length, bool expectedValid)
    {
        var result = _validator.Validate(new ReviewVerificationRequestDto
        {
            Decision = "reject",
            ReviewNote = new string('a', length)
        });

        result.IsValid.Should().Be(expectedValid);
    }
}
