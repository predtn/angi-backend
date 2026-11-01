using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Unit tests for GetModBlogsRequestDtoValidator.</summary>
public sealed class GetModBlogsRequestDtoValidatorTests
{
    private readonly GetModBlogsRequestDtoValidator _validator = new();

    [Theory]
    [InlineData(1, 20, null, true)]
    [InlineData(1, 20, "published", true)]
    [InlineData(1, 20, "hidden", true)]
    [InlineData(1, 20, "removed", true)]
    [InlineData(0, 20, "published", false)]
    [InlineData(1, 0, "published", false)]
    [InlineData(1, 101, "published", false)]
    [InlineData(1, 20, "invalid_status", false)]
    public void Validates_Pagination_And_Filters(int page, int pageSize, string? status, bool expectedValid)
    {
        var request = new GetModBlogsRequestDto
        {
            Page = page,
            PageSize = pageSize,
            Status = status
        };

        var result = _validator.Validate(request);
        result.IsValid.Should().Be(expectedValid);
    }
}
