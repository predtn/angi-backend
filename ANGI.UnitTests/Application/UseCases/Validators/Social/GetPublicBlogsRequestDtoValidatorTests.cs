using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Validators.Social;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Social;

/// <summary>Unit tests for BLOG-01 request validator.</summary>
public sealed class GetPublicBlogsRequestDtoValidatorTests
{
    private readonly GetPublicBlogsRequestDtoValidator _validator = new();

    // TEST-01: Verify Validate when default request; should be valid.
    /// <summary>Verify Validate when default request; should be valid.</summary>
    [Fact]
    public void Validate_WhenDefaultRequest_ShouldBeValid()
    {
        var request = new GetPublicBlogsRequestDto();

        var result = _validator.Validate(request);

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Verify Validate when valid parameters provided; should be valid.
    /// <summary>Verify Validate when valid parameters provided; should be valid.</summary>
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

    // TEST-03: Verify Validate when author id is not positive; should fail.
    /// <summary>Verify Validate when author id is not positive; should fail.</summary>
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

    // TEST-04: Verify Validate when sort is unsupported; should fail.
    /// <summary>Verify Validate when sort is unsupported; should fail.</summary>
    [Fact]
    public void Validate_WhenSortIsUnsupported_ShouldFail()
    {
        var request = new GetPublicBlogsRequestDto { Sort = "trending" };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetPublicBlogsRequestDto.Sort));
    }

    // TEST-05: Verify Validate when page is less than one; should fail.
    /// <summary>Verify Validate when page is less than one; should fail.</summary>
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

    // TEST-06: Verify Validate when page size is out of range; should fail.
    /// <summary>Verify Validate when page size is out of range; should fail.</summary>
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
