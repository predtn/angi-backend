using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Validators.Social;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Social;

/// <summary>Unit tests for CreateBlogCommentRequestDtoValidator.</summary>
public sealed class CreateBlogCommentRequestDtoValidatorTests
{
    private readonly CreateBlogCommentRequestDtoValidator _validator = new();

    // TEST-01: Verify Validate when valid; should not have errors.
    /// <summary>Verify Validate when valid; should not have errors.</summary>
    [Fact]
    public void Validate_WhenValid_ShouldNotHaveErrors()
    {
        var dto = new CreateBlogCommentRequestDto { Content = "Bài viết rất hay!" };
        var result = _validator.Validate(dto);
        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Verify Validate when content empty or null; should have validation error.
    /// <summary>Verify Validate when content empty or null; should have validation error.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_WhenContentEmptyOrNull_ShouldHaveValidationError(string? content)
    {
        var dto = new CreateBlogCommentRequestDto { Content = content! };
        var result = _validator.Validate(dto);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBlogCommentRequestDto.Content));
    }

    // TEST-03: Verify Validate when content exceeds1000 chars; should have validation error.
    /// <summary>Verify Validate when content exceeds1000 chars; should have validation error.</summary>
    [Fact]
    public void Validate_WhenContentExceeds1000Chars_ShouldHaveValidationError()
    {
        var dto = new CreateBlogCommentRequestDto { Content = new string('a', 1001) };
        var result = _validator.Validate(dto);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBlogCommentRequestDto.Content));
    }
}
