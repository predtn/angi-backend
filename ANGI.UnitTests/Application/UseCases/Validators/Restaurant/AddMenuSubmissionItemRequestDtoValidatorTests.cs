using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Verifies AddMenuSubmissionItemRequestDtoValidator enforces the dish proposal limits, price precision and tag rules.</summary>
public sealed class AddMenuSubmissionItemRequestDtoValidatorTests
{
    // TEST-01: Accept a complete dish proposal matching OWN-14 bounds.
    /// <summary>Verifies a valid add-item request passes every rule.</summary>
    [Fact]
    public async Task ValidateAsync_WithValidRequest_ShouldBeValid()
    {
        // STEP-01: Validate AddMenuSubmissionItemRequestDto { Name, Description, DishForm, ... } and keep result.
        var result = await new AddMenuSubmissionItemRequestDtoValidator().ValidateAsync(new AddMenuSubmissionItemRequestDto
        {
            Name = "Mì Quảng",
            Description = "Sợi mì vàng, nước lèo sệt và thịt gà ta.",
            DishForm = "dry",
            Price = 40000,
            TagIds = [1, 2]
        });

        // STEP-02: Assert result.IsValid is true.
        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject descriptions shorter than the documented minimum.
    /// <summary>Verifies the 30-character OWN-14 description constraint.</summary>
    [Fact]
    public async Task ValidateAsync_WithShortDescription_ShouldRejectDescription()
    {
        // STEP-01: Validate AddMenuSubmissionItemRequestDto { Name, Description, DishForm, ... } and keep result.
        var result = await new AddMenuSubmissionItemRequestDtoValidator().ValidateAsync(new AddMenuSubmissionItemRequestDto
        {
            Name = "Mì Quảng",
            Description = "Quá ngắn",
            DishForm = "dry",
            Price = 40000
        });

        // STEP-02: Assert result.Errors contains a matching item.
        result.Errors.Should().Contain(error => error.PropertyName == "Description");
    }

    // TEST-03: Match the numeric(12,0) lower, upper, overflow, fraction, and negative boundaries.
    /// <summary>Ensures accepted prices always fit the numeric(12,0) column.</summary>
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
        // STEP-01: Create request through ValidRequest and set request.Price to decimal.Parse().
        var request = ValidRequest();
        request.Price = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        // STEP-02: Validate request and keep result.
        var result = await new AddMenuSubmissionItemRequestDtoValidator().ValidateAsync(request);

        // STEP-03: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-04: Measure description length after trimming surrounding whitespace.
    /// <summary>Ensures padding cannot satisfy the minimum description length.</summary>
    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public async Task ValidateAsync_ShouldMeasureDescriptionAfterTrim(int contentLength, bool expectedValid)
    {
        // STEP-01: Create request through ValidRequest and set request.Description to " {new string()} ".
        var request = ValidRequest();
        request.Description = $"  {new string('a', contentLength)}  ";

        // STEP-02: Validate request and keep result.
        var result = await new AddMenuSubmissionItemRequestDtoValidator().ValidateAsync(request);

        // STEP-03: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-05: Allow omitted tags and reject duplicate or negative tag identifiers.
    /// <summary>Ensures tags are optional but never duplicated or negative.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("1", "1,1")]
    [InlineData("1", "-1")]
    public async Task ValidateAsync_WithInvalidTags_ShouldRejectTagIds(string? first, string? second)
    {
        // STEP-01: Create request through ValidRequest and set request.TagIds to a test value.
        var request = ValidRequest();
        request.TagIds = first is null
            ? null
            : second == "1,1" ? [(short)1, (short)1] : [(short)1, (short)-1];

        // STEP-02: Validate request and keep result.
        var result = await new AddMenuSubmissionItemRequestDtoValidator().ValidateAsync(request);

        // STEP-03: Assert result.IsValid is first is null.
        result.IsValid.Should().Be(first is null);
    }

    /// <summary>Creates a valid add-item request without tags or cover.</summary>
    private static AddMenuSubmissionItemRequestDto ValidRequest() => new()
    {
        Name = "Valid dish",
        Description = new string('a', 30),
        DishForm = "dry",
        Price = 40_000,
        TagIds = []
    };
}
