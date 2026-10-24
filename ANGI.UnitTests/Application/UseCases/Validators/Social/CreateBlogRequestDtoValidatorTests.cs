using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Validators.Social;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Social;

/// <summary>Boundary tests for the BLOG-04 request validator.</summary>
public sealed class CreateBlogRequestDtoValidatorTests
{
    private readonly CreateBlogRequestDtoValidator _validator = new();

    // TEST-01: Accept title lengths 1 and 255, zero or 20 images, and positive optional identifiers.
    /// <summary>Verifies every inclusive BLOG-04 request boundary.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(255, 20)]
    public async Task Validate_AtInclusiveBoundaries_ShouldSucceed(int titleLength, int imageCount)
    {
        var request = ValidRequest();
        request.Title = new string('a', titleLength);
        request.CoverMediaId = 1;
        request.RoadmapId = 1;
        request.ImageMediaIds = Enumerable.Range(1, imageCount).Select(value => (long)value).ToArray();

        var result = await _validator.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject missing required text exactly once per field.
    /// <summary>Verifies null, empty, and whitespace title/content regions cannot reach BLOG-04.</summary>
    [Theory]
    [InlineData(null, "content", "Title")]
    [InlineData(" ", "content", "Title")]
    [InlineData("title", null, "Content")]
    [InlineData("title", " ", "Content")]
    public async Task Validate_WithMissingText_ShouldReportOneFieldError(
        string? title,
        string? content,
        string property)
    {
        var request = ValidRequest();
        request.Title = title!;
        request.Content = content!;

        var result = await _validator.ValidateAsync(request);

        result.Errors.Should().ContainSingle(error => error.PropertyName == property);
    }

    // TEST-03: Reject title length 256 and an image collection of 21 elements.
    /// <summary>Verifies values immediately above both documented BLOG-04 maxima.</summary>
    [Fact]
    public async Task Validate_AboveMaximums_ShouldRejectTitleAndImages()
    {
        var request = ValidRequest();
        request.Title = new string('a', 256);
        request.ImageMediaIds = Enumerable.Range(1, 21).Select(value => (long)value).ToArray();

        var result = await _validator.ValidateAsync(request);

        result.Errors.Select(error => error.PropertyName).Should().BeEquivalentTo("Title", "ImageMediaIds");
    }

    // TEST-04: Reject zero or negative cover, image, and roadmap identifiers.
    /// <summary>Verifies every invalid identifier region is assigned to its request field.</summary>
    [Fact]
    public async Task Validate_WithNonPositiveIdentifiers_ShouldRejectEveryAffectedField()
    {
        var request = ValidRequest();
        request.CoverMediaId = 0;
        request.RoadmapId = -1;
        request.ImageMediaIds = [1, 0, -2];

        var result = await _validator.ValidateAsync(request);

        result.Errors.Select(error => error.PropertyName)
            .Should().BeEquivalentTo("CoverMediaId", "RoadmapId", "ImageMediaIds");
    }

    // TEST-05: Reject duplicate image identifiers that would violate the blog_images composite key.
    /// <summary>Verifies duplicate gallery references fail before persistence.</summary>
    [Fact]
    public async Task Validate_WithDuplicateImageIdentifiers_ShouldRejectImages()
    {
        var request = ValidRequest();
        request.ImageMediaIds = [7, 7];

        var result = await _validator.ValidateAsync(request);

        result.Errors.Should().ContainSingle(error => error.PropertyName == "ImageMediaIds");
    }

    /// <summary>Creates the smallest valid BLOG-04 request for boundary mutation.</summary>
    private static CreateBlogRequestDto ValidRequest() => new()
    {
        Title = "Bài viết",
        Content = "Nội dung"
    };
}
