using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Validators.Social;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Social;

/// <summary>Unit tests for <see cref="GetBlogCommentsRequestDtoValidator"/>.</summary>
public sealed class GetBlogCommentsRequestDtoValidatorTests
{
    private readonly GetBlogCommentsRequestDtoValidator _validator = new();

    // TEST-01: Verify ValidateAsync with valid defaults; should be valid.
    /// <summary>Verify ValidateAsync with valid defaults; should be valid.</summary>
    [Fact]
    public async Task ValidateAsync_WithValidDefaults_ShouldBeValid()
    {
        var result = await _validator.ValidateAsync(new GetBlogCommentsRequestDto());

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Verify ValidateAsync with invalid page; should report page error.
    /// <summary>Verify ValidateAsync with invalid page; should report page error.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ValidateAsync_WithInvalidPage_ShouldReportPageError(int page)
    {
        var result = await _validator.ValidateAsync(new GetBlogCommentsRequestDto { Page = page, PageSize = 20 });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(GetBlogCommentsRequestDto.Page));
    }

    // TEST-03: Verify ValidateAsync with invalid page size; should report page size error.
    /// <summary>Verify ValidateAsync with invalid page size; should report page size error.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task ValidateAsync_WithInvalidPageSize_ShouldReportPageSizeError(int pageSize)
    {
        var result = await _validator.ValidateAsync(new GetBlogCommentsRequestDto { Page = 1, PageSize = pageSize });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(GetBlogCommentsRequestDto.PageSize));
    }
}
