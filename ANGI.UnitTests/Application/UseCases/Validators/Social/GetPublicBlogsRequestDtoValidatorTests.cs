using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Validators.Social;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Social;

/// <summary>Unit tests for BLOG-01 request validator.</summary>
public sealed class GetPublicBlogsRequestDtoValidatorTests
{
    private readonly GetPublicBlogsRequestDtoValidator _validator = new();

    [Fact]
    public void Validate_WhenDefaultRequest_ShouldBeValid()
    {
        var request = new GetPublicBlogsRequestDto();

        var result = _validator.Validate(request);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("newest")]
    [InlineData("popular")]
    [InlineData("NEWEST")]
    [InlineData("POPULAR")]
    public void Validate_WhenValidParametersProvided_ShouldBeValid(string sort)
    {
        var request = new GetPublicBlogsRequestDto
        {
            Q = "bún bò",
            AuthorId = 5,
            HasRoadmap = true,
            Sort = sort,
            Page = 2,
            PageSize = 50
        };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Validate_WhenAuthorIdIsNotPositive_ShouldFail(int authorId)
    {
        var request = new GetPublicBlogsRequestDto { AuthorId = authorId };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetPublicBlogsRequestDto.AuthorId));
    }

    [Fact]
    public void Validate_WhenSortIsUnsupported_ShouldFail()
    {
        var request = new GetPublicBlogsRequestDto { Sort = "trending" };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetPublicBlogsRequestDto.Sort));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenPageIsLessThanOne_ShouldFail(int page)
    {
        var request = new GetPublicBlogsRequestDto { Page = page };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetPublicBlogsRequestDto.Page));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(101)]
    public void Validate_WhenPageSizeIsOutOfRange_ShouldFail(int pageSize)
    {
        var request = new GetPublicBlogsRequestDto { PageSize = pageSize };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetPublicBlogsRequestDto.PageSize));
    }
}
