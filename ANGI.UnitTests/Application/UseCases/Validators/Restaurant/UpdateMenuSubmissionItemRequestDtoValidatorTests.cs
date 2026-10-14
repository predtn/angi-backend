using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

public sealed class UpdateMenuSubmissionItemRequestDtoValidatorTests
{
    // TEST-01: Accept an explicit null cover because OWN-15 uses it to remove the image.
    /// <summary>Verifies nullable cover patch semantics remain valid.</summary>
    [Fact]
    public async Task ValidateAsync_WithExplicitNullCover_ShouldBeValid()
    {
        var request = new UpdateMenuSubmissionItemRequestDto { CoverMediaId = null };

        var result = await new UpdateMenuSubmissionItemRequestDtoValidator().ValidateAsync(request);

        result.IsValid.Should().BeTrue();
        request.CoverMediaIdSpecified.Should().BeTrue();
    }

    // TEST-02: Reject an explicitly null price while allowing an omitted price.
    /// <summary>Verifies required value semantics for a supplied OWN-15 price.</summary>
    [Fact]
    public async Task ValidateAsync_WithExplicitNullPrice_ShouldRejectPrice()
    {
        var result = await new UpdateMenuSubmissionItemRequestDtoValidator().ValidateAsync(
            new UpdateMenuSubmissionItemRequestDto { Price = null });

        result.Errors.Should().Contain(error => error.PropertyName == "Price");
    }

    // TEST-03: Accept an empty PATCH because every field is optional.
    [Fact]
    public async Task ValidateAsync_WithEmptyPatch_ShouldBeValid()
    {
        var result = await new UpdateMenuSubmissionItemRequestDtoValidator().ValidateAsync(
            new UpdateMenuSubmissionItemRequestDto());

        result.IsValid.Should().BeTrue();
    }

    // TEST-04: Reject explicit null for every non-nullable patch field.
    [Theory]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("dish_form")]
    [InlineData("price")]
    [InlineData("tag_ids")]
    [InlineData("sort_order")]
    public async Task ValidateAsync_WithExplicitNullNonNullableField_ShouldRejectField(string field)
    {
        var request = new UpdateMenuSubmissionItemRequestDto();
        switch (field)
        {
            case "name": request.Name = null; break;
            case "description": request.Description = null; break;
            case "dish_form": request.DishForm = null; break;
            case "price": request.Price = null; break;
            case "tag_ids": request.TagIds = null; break;
            case "sort_order": request.SortOrder = null; break;
        }

        var result = await new UpdateMenuSubmissionItemRequestDtoValidator().ValidateAsync(request);

        result.IsValid.Should().BeFalse();
    }

    // TEST-05: Match the numeric(12,0) price boundaries for PATCH.
    [Theory]
    [InlineData("0", true)]
    [InlineData("999999999999", true)]
    [InlineData("1000000000000", false)]
    [InlineData("0.1", false)]
    public async Task ValidateAsync_WithPriceBoundary_ShouldMatchNumericTwelveZero(
        string value,
        bool expectedValid)
    {
        var request = new UpdateMenuSubmissionItemRequestDto
        {
            Price = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
        };

        var result = await new UpdateMenuSubmissionItemRequestDtoValidator().ValidateAsync(request);

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-06: Measure patched description length after trimming whitespace.
    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public async Task ValidateAsync_ShouldMeasureDescriptionAfterTrim(int length, bool expectedValid)
    {
        var request = new UpdateMenuSubmissionItemRequestDto
        {
            Description = $"  {new string('a', length)}  "
        };

        var result = await new UpdateMenuSubmissionItemRequestDtoValidator().ValidateAsync(request);

        result.IsValid.Should().Be(expectedValid);
    }
}
