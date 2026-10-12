using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.Test.Application.UseCases.Validators.Restaurant;

public sealed class ListMenuSubmissionsRequestDtoValidatorTests
{
    // TEST-01: Reject unsupported status values and oversized pages.
    /// <summary>Verifies OWN-12 query bounds before repository access.</summary>
    [Fact]
    public async Task ValidateAsync_WithInvalidQuery_ShouldRejectStatusAndPageSize()
    {
        var result = await new ListMenuSubmissionsRequestDtoValidator().ValidateAsync(new ListMenuSubmissionsRequestDto
        {
            Status = "unknown",
            PageSize = 101
        });

        result.Errors.Select(error => error.PropertyName)
            .Should().Contain(["Status", "PageSize"]);
    }

    // TEST-02: Accept no filter and every supported status regardless of casing.
    [Theory]
    [InlineData(null)]
    [InlineData("draft")]
    [InlineData("PENDING")]
    [InlineData("Approved")]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public async Task ValidateAsync_WithSupportedStatus_ShouldBeValid(string? status)
    {
        var result = await new ListMenuSubmissionsRequestDtoValidator().ValidateAsync(
            new ListMenuSubmissionsRequestDto { Status = status });

        result.IsValid.Should().BeTrue();
    }

    // TEST-03: Enforce page and page-size boundaries from the API contract.
    [Theory]
    [InlineData(0, 20, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 100, true)]
    [InlineData(1, 101, false)]
    public async Task ValidateAsync_WithPaginationBoundary_ShouldMatchContract(
        int page,
        int pageSize,
        bool expectedValid)
    {
        var result = await new ListMenuSubmissionsRequestDtoValidator().ValidateAsync(
            new ListMenuSubmissionsRequestDto { Page = page, PageSize = pageSize });

        result.IsValid.Should().Be(expectedValid);
    }
}
