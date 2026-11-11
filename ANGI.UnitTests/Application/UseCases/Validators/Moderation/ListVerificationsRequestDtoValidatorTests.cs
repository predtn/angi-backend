using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Verifies ListVerificationsRequestDtoValidator accepts only documented statuses and paging ranges.</summary>
public sealed class ListVerificationsRequestDtoValidatorTests
{
    private readonly ListVerificationsRequestDtoValidator _validator = new();

    // TEST-01: Accept every documented status and both paging boundaries.
    /// <summary>Verifies the complete valid partition for MOD-V1 query parameters.</summary>
    [Theory]
    [InlineData("pending", 1, 1)]
    [InlineData("approved", 1, 100)]
    [InlineData("rejected", 2, 20)]
    [InlineData("needs_more_info", 3, 50)]
    public void Validate_WithDocumentedValues_ShouldSucceed(string status, int page, int pageSize)
    {
        // STEP-01: Validate ListVerificationsRequestDto { Status = status, Page = page, PageSize = pageSize } and keep result.
        var result = _validator.Validate(new ListVerificationsRequestDto
        {
            Status = status,
            Page = page,
            PageSize = pageSize
        });

        // STEP-02: Assert result.IsValid is true.
        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject missing, cancelled, and unknown statuses.
    /// <summary>Verifies statuses outside the MOD-V1 filter contract are rejected.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("cancelled")]
    [InlineData("unknown")]
    public void Validate_WithUnsupportedStatus_ShouldFail(string status)
    {
        // STEP-01: Validate ListVerificationsRequestDto { Status = status } and keep result.
        var result = _validator.Validate(new ListVerificationsRequestDto { Status = status });

        // STEP-02: Assert result.Errors contains a matching item.
        result.Errors.Should().Contain(error => error.PropertyName == nameof(ListVerificationsRequestDto.Status));
    }

    // TEST-03: Reject page values below one.
    /// <summary>Verifies the lower page boundary.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithInvalidPage_ShouldFail(int page)
    {
        // STEP-01: Validate ListVerificationsRequestDto { Page = page } and keep result.
        var result = _validator.Validate(new ListVerificationsRequestDto { Page = page });

        // STEP-02: Assert result.Errors contains a matching item.
        result.Errors.Should().Contain(error => error.PropertyName == nameof(ListVerificationsRequestDto.Page));
    }

    // TEST-04: Reject page sizes outside one through one hundred.
    /// <summary>Verifies both excluded page-size regions.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_WithInvalidPageSize_ShouldFail(int pageSize)
    {
        // STEP-01: Validate ListVerificationsRequestDto { PageSize = pageSize } and keep result.
        var result = _validator.Validate(new ListVerificationsRequestDto { PageSize = pageSize });

        // STEP-02: Assert result.Errors contains a matching item.
        result.Errors.Should().Contain(error => error.PropertyName == nameof(ListVerificationsRequestDto.PageSize));
    }
}
