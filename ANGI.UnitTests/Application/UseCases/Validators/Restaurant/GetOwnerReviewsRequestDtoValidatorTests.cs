using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Verifies GetOwnerReviewsRequestDtoValidator enforces the rating filter and paging ranges.</summary>
public sealed class GetOwnerReviewsRequestDtoValidatorTests
{
    private readonly GetOwnerReviewsRequestDtoValidator _validator = new();

    // TEST-01: A request with valid paging and rating filters has no errors.
    /// <summary>Ensures a request with valid filters is not blocked.</summary>
    [Fact]
    public void Validate_WhenRequestIsValid_ShouldNotHaveErrors()
    {
        var request = new GetOwnerReviewsRequestDto
        {
            Page = 1,
            PageSize = 20,
            Rating = 4,
            Replied = true
        };

        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    // TEST-02: A rating filter outside 1..5 fails; covers 0 (min - 1) and 6 (max + 1).
    /// <summary>Ensures the rating filter is limited to 1..5 at both boundaries.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Validate_WhenRatingOutOfBounds_ShouldHaveError(short rating)
    {
        var request = new GetOwnerReviewsRequestDto { Rating = rating };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Rating);
    }

    // TEST-03: A page below 1 fails; covers 0 and -1.
    /// <summary>Ensures pages start at 1.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenPageLessThanOne_ShouldHaveError(int page)
    {
        var request = new GetOwnerReviewsRequestDto { Page = page };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Page);
    }

    // TEST-04: A page size outside 1..100 fails; covers 0 (min - 1) and 101 (max + 1).
    /// <summary>Ensures the page size is limited to 1..100 at both boundaries.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_WhenPageSizeOutOfRange_ShouldHaveError(int pageSize)
    {
        var request = new GetOwnerReviewsRequestDto { PageSize = pageSize };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.PageSize);
    }
}
