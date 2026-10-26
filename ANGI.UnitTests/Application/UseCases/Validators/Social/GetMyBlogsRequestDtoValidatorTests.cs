using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Validators.Social;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Social;

public sealed class GetMyBlogsRequestDtoValidatorTests
{
    private readonly GetMyBlogsRequestDtoValidator _validator = new();

    // TEST-01: Verify Validate with default values; should pass.
    /// <summary>Verify Validate with default values; should pass.</summary>
    [Fact]
    public void Validate_WithDefaultValues_ShouldPass()
    {
        var request = new GetMyBlogsRequestDto();
        var result = _validator.Validate(request);
        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Verify Validate with valid status; should pass.
    /// <summary>Verify Validate with valid status; should pass.</summary>
    [Theory]
    [InlineData("published")]
    [InlineData("hidden")]
    [InlineData("removed")]
    [InlineData("PUBLISHED")]
    [InlineData("Hidden")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithValidStatus_ShouldPass(string? status)
    {
        var request = new GetMyBlogsRequestDto { Status = status, Page = 1, PageSize = 20 };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeTrue();
    }

    // TEST-03: Verify Validate with invalid page; should fail.
    /// <summary>Verify Validate with invalid page; should fail.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_WithInvalidPage_ShouldFail(int page)
    {
        var request = new GetMyBlogsRequestDto { Page = page, PageSize = 20 };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetMyBlogsRequestDto.Page));
    }

    // TEST-04: Verify Validate with invalid page size; should fail.
    /// <summary>Verify Validate with invalid page size; should fail.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(200)]
    public void Validate_WithInvalidPageSize_ShouldFail(int pageSize)
    {
        var request = new GetMyBlogsRequestDto { Page = 1, PageSize = pageSize };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetMyBlogsRequestDto.PageSize));
    }

    // TEST-05: Verify Validate with invalid status; should fail.
    /// <summary>Verify Validate with invalid status; should fail.</summary>
    [Theory]
    [InlineData("draft")]
    [InlineData("pending")]
    [InlineData("rejected")]
    [InlineData("unknown")]
    public void Validate_WithInvalidStatus_ShouldFail(string status)
    {
        var request = new GetMyBlogsRequestDto { Status = status, Page = 1, PageSize = 20 };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetMyBlogsRequestDto.Status));
    }
}
