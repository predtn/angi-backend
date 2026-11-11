using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Verifies ListMenuSubmissionsRequestDtoValidator accepts supported statuses and enforces paging ranges.</summary>
public sealed class ListMenuSubmissionsRequestDtoValidatorTests
{
    // TEST-01: Reject unsupported status values and oversized pages.
    /// <summary>Verifies OWN-12 query bounds before repository access.</summary>
    [Fact]
    public async Task ValidateAsync_WithInvalidQuery_ShouldRejectStatusAndPageSize()
    {
        // STEP-01: Validate ListMenuSubmissionsRequestDto { Status = "unknown", PageSize = 101 } and keep result.
        var result = await new ListMenuSubmissionsRequestDtoValidator().ValidateAsync(new ListMenuSubmissionsRequestDto
        {
            Status = "unknown",
            PageSize = 101
        });

        // STEP-02: Assert result.Errors.Select(filtered) contains the expected items.
        result.Errors.Select(error => error.PropertyName)
            .Should().Contain(["Status", "PageSize"]);
    }

    // TEST-02: Accept no filter and every supported status regardless of casing.
    /// <summary>Ensures every supported status is accepted in any letter case.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("draft")]
    [InlineData("PENDING")]
    [InlineData("Approved")]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public async Task ValidateAsync_WithSupportedStatus_ShouldBeValid(string? status)
    {
        // STEP-01: Validate ListMenuSubmissionsRequestDto { Status = status } and keep result.
        var result = await new ListMenuSubmissionsRequestDtoValidator().ValidateAsync(
            new ListMenuSubmissionsRequestDto { Status = status });

        // STEP-02: Assert result.IsValid is true.
        result.IsValid.Should().BeTrue();
    }

    // TEST-03: Enforce page and page-size boundaries from the API contract.
    /// <summary>Ensures paging is accepted or rejected exactly at the documented boundaries.</summary>
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
        // STEP-01: Validate ListMenuSubmissionsRequestDto { Page = page, PageSize = pageSize } and keep result.
        var result = await new ListMenuSubmissionsRequestDtoValidator().ValidateAsync(
            new ListMenuSubmissionsRequestDto { Page = page, PageSize = pageSize });

        // STEP-02: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }
}
