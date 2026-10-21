using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Validators.Social;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Social;

public sealed class CreateBlogRequestDtoValidatorTests
{
    private readonly CreateBlogRequestDtoValidator _validator = new();

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveAnyErrors()
    {
        var request = new CreateBlogRequestDto
        {
            Title = "Hành trình ẩm thực Đà Nẵng",
            Content = "<p>Nội dung bài viết...</p>",
            CoverMediaId = 10,
            ImageMediaIds = [11, 12],
            RoadmapId = 88
        };

        var result = _validator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_WithEmptyTitle_ShouldHaveValidationError(string? title)
    {
        var request = new CreateBlogRequestDto
        {
            Title = title!,
            Content = "Nội dung bài viết"
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Title)
            .WithErrorMessage("Tiêu đề bài viết là bắt buộc.");
    }

    [Fact]
    public void Validate_WithTitleExceeding255Chars_ShouldHaveValidationError()
    {
        var request = new CreateBlogRequestDto
        {
            Title = new string('a', 256),
            Content = "Nội dung bài viết"
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Title)
            .WithErrorMessage("Tiêu đề bài viết không được vượt quá 255 ký tự.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_WithEmptyContent_ShouldHaveValidationError(string? content)
    {
        var request = new CreateBlogRequestDto
        {
            Title = "Tiêu đề bài viết",
            Content = content!
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Content)
            .WithErrorMessage("Nội dung bài viết là bắt buộc.");
    }

    [Fact]
    public void Validate_WithInvalidCoverMediaId_ShouldHaveValidationError()
    {
        var request = new CreateBlogRequestDto
        {
            Title = "Tiêu đề",
            Content = "Nội dung",
            CoverMediaId = 0
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.CoverMediaId)
            .WithErrorMessage("ID ảnh bìa không hợp lệ.");
    }

    [Fact]
    public void Validate_WithMoreThan20Images_ShouldHaveValidationError()
    {
        var request = new CreateBlogRequestDto
        {
            Title = "Tiêu đề",
            Content = "Nội dung",
            ImageMediaIds = Enumerable.Range(1, 21).Select(i => (long)i).ToList()
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.ImageMediaIds)
            .WithErrorMessage("Số lượng ảnh đính kèm không được vượt quá 20 ảnh.");
    }

    [Fact]
    public void Validate_WithInvalidRoadmapId_ShouldHaveValidationError()
    {
        var request = new CreateBlogRequestDto
        {
            Title = "Tiêu đề",
            Content = "Nội dung",
            RoadmapId = -1
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.RoadmapId)
            .WithErrorMessage("ID lộ trình không hợp lệ.");
    }
}
