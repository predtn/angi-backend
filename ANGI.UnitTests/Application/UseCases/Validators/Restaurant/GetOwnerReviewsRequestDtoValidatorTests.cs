using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

public sealed class GetOwnerReviewsRequestDtoValidatorTests
{
    private readonly GetOwnerReviewsRequestDtoValidator _validator = new();

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

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Validate_WhenRatingOutOfBounds_ShouldHaveError(short rating)
    {
        var request = new GetOwnerReviewsRequestDto { Rating = rating };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Rating);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenPageLessThanOne_ShouldHaveError(int page)
    {
        var request = new GetOwnerReviewsRequestDto { Page = page };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Page);
    }

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
