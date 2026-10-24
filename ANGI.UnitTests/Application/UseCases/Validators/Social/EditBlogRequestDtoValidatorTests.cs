using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Validators.Social;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Social;

/// <summary>Boundary tests for the BLOG-06 request validator.</summary>
public sealed class EditBlogRequestDtoValidatorTests
{
    private readonly EditBlogRequestDtoValidator _validator = new();

    // TEST-01: An empty request where no fields are specified is valid for PATCH.
    [Fact]
    public async Task Validate_WhenNoFieldsSpecified_ShouldSucceed()
    {
        var request = new EditBlogRequestDto();

        var result = await _validator.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Accept title lengths 1 and 255, null cover, and 20 distinct positive image IDs.
    [Theory]
    [InlineData(1, 0)]
    [InlineData(255, 20)]
    public async Task Validate_AtInclusiveBoundaries_ShouldSucceed(int titleLength, int imageCount)
    {
        var request = new EditBlogRequestDto
        {
            Title = new string('a', titleLength),
            Content = "Nội dung cập nhật",
            CoverMediaId = null,
            ImageMediaIds = Enumerable.Range(1, imageCount).Select(value => (long)value).ToArray()
        };

        var result = await _validator.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }

    // TEST-03: Reject empty/whitespace title or content when specified.
    [Theory]
    [InlineData(null, "Title")]
    [InlineData("", "Title")]
    [InlineData("   ", "Title")]
    public async Task Validate_WithEmptyTitle_ShouldReportTitleError(string? title, string property)
    {
        var request = new EditBlogRequestDto { Title = title };

        var result = await _validator.ValidateAsync(request);

        result.Errors.Should().ContainSingle(error => error.PropertyName == property);
    }

    [Theory]
    [InlineData(null, "Content")]
    [InlineData("", "Content")]
    [InlineData("   ", "Content")]
    public async Task Validate_WithEmptyContent_ShouldReportContentError(string? content, string property)
    {
        var request = new EditBlogRequestDto { Content = content };

        var result = await _validator.ValidateAsync(request);

        result.Errors.Should().ContainSingle(error => error.PropertyName == property);
    }

    // TEST-04: Reject title > 255 chars and images > 20 elements.
    [Fact]
    public async Task Validate_AboveMaximums_ShouldRejectTitleAndImages()
    {
        var request = new EditBlogRequestDto
        {
            Title = new string('a', 256),
            ImageMediaIds = Enumerable.Range(1, 21).Select(value => (long)value).ToArray()
        };

        var result = await _validator.ValidateAsync(request);

        result.Errors.Select(error => error.PropertyName).Should().BeEquivalentTo("Title", "ImageMediaIds");
    }

    // TEST-05: Reject non-positive cover media and non-positive image media IDs.
    [Fact]
    public async Task Validate_WithNonPositiveIdentifiers_ShouldRejectAffectedFields()
    {
        var request = new EditBlogRequestDto
        {
            CoverMediaId = 0,
            ImageMediaIds = [1, 0, -2]
        };

        var result = await _validator.ValidateAsync(request);

        result.Errors.Select(error => error.PropertyName).Should().BeEquivalentTo("CoverMediaId", "ImageMediaIds");
    }

    // TEST-06: Reject duplicate image IDs.
    [Fact]
    public async Task Validate_WithDuplicateImageIdentifiers_ShouldRejectImages()
    {
        var request = new EditBlogRequestDto
        {
            ImageMediaIds = [5, 5]
        };

        var result = await _validator.ValidateAsync(request);

        result.Errors.Should().ContainSingle(error => error.PropertyName == "ImageMediaIds");
    }
}
