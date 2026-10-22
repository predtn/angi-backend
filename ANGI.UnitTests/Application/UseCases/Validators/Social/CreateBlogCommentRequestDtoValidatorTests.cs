using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Validators.Social;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Social;

/// <summary>Boundary tests for the BLOG-10 request validator.</summary>
public sealed class CreateBlogCommentRequestDtoValidatorTests
{
    private readonly CreateBlogCommentRequestDtoValidator _validator = new();

    // TEST-01: Valid content within length 1..1000 should pass validation.
    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(1000)]
    public async Task Validate_WithValidContentLength_ShouldPass(int length)
    {
        var request = new CreateBlogCommentRequestDto
        {
            Content = new string('x', length)
        };

        var result = await _validator.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Missing, empty, or whitespace content should fail validation.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validate_WithEmptyOrWhitespaceContent_ShouldFail(string? content)
    {
        var request = new CreateBlogCommentRequestDto
        {
            Content = content!
        };

        var result = await _validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBlogCommentRequestDto.Content));
    }

    // TEST-03: Content exceeding 1000 characters should fail validation.
    [Fact]
    public async Task Validate_WithContentExceeding1000Chars_ShouldFail()
    {
        var request = new CreateBlogCommentRequestDto
        {
            Content = new string('x', 1001)
        };

        var result = await _validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBlogCommentRequestDto.Content)
            && e.ErrorMessage.Contains("1000"));
    }
}
