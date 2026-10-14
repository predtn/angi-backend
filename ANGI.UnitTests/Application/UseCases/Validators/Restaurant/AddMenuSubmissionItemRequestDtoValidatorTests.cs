using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

public sealed class AddMenuSubmissionItemRequestDtoValidatorTests
{
    // TEST-01: Accept a complete dish proposal matching OWN-14 bounds.
    /// <summary>Verifies a valid add-item request passes every rule.</summary>
    [Fact]
    public async Task ValidateAsync_WithValidRequest_ShouldBeValid()
    {
        var result = await new AddMenuSubmissionItemRequestDtoValidator().ValidateAsync(new AddMenuSubmissionItemRequestDto
        {
            Name = "Mì Quảng",
            Description = "Sợi mì vàng, nước lèo sệt và thịt gà ta.",
            DishForm = "dry",
            Price = 40000,
            TagIds = [1, 2]
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject descriptions shorter than the documented minimum.
    /// <summary>Verifies the 30-character OWN-14 description constraint.</summary>
    [Fact]
    public async Task ValidateAsync_WithShortDescription_ShouldRejectDescription()
    {
        var result = await new AddMenuSubmissionItemRequestDtoValidator().ValidateAsync(new AddMenuSubmissionItemRequestDto
        {
            Name = "Mì Quảng",
            Description = "Quá ngắn",
            DishForm = "dry",
            Price = 40000
        });

        result.Errors.Should().Contain(error => error.PropertyName == "Description");
    }

    // TEST-03: Match the numeric(12,0) lower, upper, overflow, fraction, and negative boundaries.
    [Theory]
    [InlineData("0", true)]
    [InlineData("999999999999", true)]
    [InlineData("1000000000000", false)]
    [InlineData("40000.5", false)]
    [InlineData("-1", false)]
    public async Task ValidateAsync_WithPriceBoundary_ShouldMatchNumericTwelveZero(
        string value,
        bool expectedValid)
    {
        var request = ValidRequest();
        request.Price = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        var result = await new AddMenuSubmissionItemRequestDtoValidator().ValidateAsync(request);

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-04: Measure description length after trimming surrounding whitespace.
    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public async Task ValidateAsync_ShouldMeasureDescriptionAfterTrim(int contentLength, bool expectedValid)
    {
        var request = ValidRequest();
        request.Description = $"  {new string('a', contentLength)}  ";

        var result = await new AddMenuSubmissionItemRequestDtoValidator().ValidateAsync(request);

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-05: Allow omitted tags and reject duplicate or negative tag identifiers.
    [Theory]
    [InlineData(null, null)]
    [InlineData("1", "1,1")]
    [InlineData("1", "-1")]
    public async Task ValidateAsync_WithInvalidTags_ShouldRejectTagIds(string? first, string? second)
    {
        var request = ValidRequest();
        request.TagIds = first is null
            ? null
            : second == "1,1" ? [(short)1, (short)1] : [(short)1, (short)-1];

        var result = await new AddMenuSubmissionItemRequestDtoValidator().ValidateAsync(request);

        result.IsValid.Should().Be(first is null);
    }

    private static AddMenuSubmissionItemRequestDto ValidRequest() => new()
    {
        Name = "Valid dish",
        Description = new string('a', 30),
        DishForm = "dry",
        Price = 40_000,
        TagIds = []
    };
}
