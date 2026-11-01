using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Verifies ReplyReviewRequestDtoValidator requires non-blank reply content within the length limit.</summary>
public sealed class ReplyReviewRequestDtoValidatorTests
{
    private readonly ReplyReviewRequestDtoValidator _validator = new();

    // TEST-01: Valid reply content has no errors.
    /// <summary>Ensures a normal reply is not blocked.</summary>
    [Fact]
    public void Validate_WhenContentIsValid_ShouldNotHaveErrors()
    {
        var request = new ReplyReviewRequestDto { Content = "Cảm ơn bạn đã đóng góp ý kiến!" };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    // TEST-02: Missing reply content fails; covers empty, whitespace and null.
    /// <summary>Ensures an empty reply is rejected.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_WhenContentIsNullOrWhitespace_ShouldHaveError(string? content)
    {
        var request = new ReplyReviewRequestDto { Content = content! };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }

    // TEST-03: Reply content of 1001 characters (max + 1) fails validation.
    /// <summary>Ensures the reply limit is applied at max + 1.</summary>
    [Fact]
    public void Validate_WhenContentExceeds1000Characters_ShouldHaveError()
    {
        var request = new ReplyReviewRequestDto { Content = new string('A', 1001) };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }
}
