using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

public sealed class ReplyReviewRequestDtoValidatorTests
{
    private readonly ReplyReviewRequestDtoValidator _validator = new();

    [Fact]
    public void Validate_WhenContentIsValid_ShouldNotHaveErrors()
    {
        var request = new ReplyReviewRequestDto { Content = "Cảm ơn bạn đã đóng góp ý kiến!" };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

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

    [Fact]
    public void Validate_WhenContentExceeds1000Characters_ShouldHaveError()
    {
        var request = new ReplyReviewRequestDto { Content = new string('A', 1001) };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }
}
