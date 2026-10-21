using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Administration;

public sealed class ListModsRequestDtoValidatorTests
{
    // TEST-01: Accept absent filters and both documented statuses at paging boundaries.
    /// <summary>Verifies all valid ADM-M1 boundary values.</summary>
    [Theory]
    [InlineData(null, 1, 1)]
    [InlineData("active", 1, 100)]
    [InlineData("deactivated", 2, 20)]
    public async Task Validate_WithValidBoundaries_ShouldPass(string? status, int page, int pageSize)
    {
        var result = await new ListModsRequestDtoValidator().ValidateAsync(new ListModsRequestDto
        {
            Status = status,
            Page = page,
            PageSize = pageSize
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject unknown or differently cased status values.
    /// <summary>Verifies ADM-M1 accepts only exact API enum strings.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("ACTIVE")]
    [InlineData("suspended")]
    public async Task Validate_WithInvalidStatus_ShouldFail(string status)
    {
        var result = await new ListModsRequestDtoValidator().ValidateAsync(new ListModsRequestDto { Status = status });

        result.Errors.Should().Contain(error => error.PropertyName == nameof(ListModsRequestDto.Status));
    }

    // TEST-03: Reject page and page-size values outside their documented ranges.
    /// <summary>Verifies lower and upper ADM-M1 pagination boundaries.</summary>
    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task Validate_WithInvalidPaging_ShouldFail(int page, int pageSize)
    {
        var result = await new ListModsRequestDtoValidator().ValidateAsync(new ListModsRequestDto
        {
            Page = page,
            PageSize = pageSize
        });

        result.IsValid.Should().BeFalse();
    }
}
