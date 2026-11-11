using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Roadmap;

/// <summary>Verifies RateRoadmapItemRequestDtoValidator accepts only the two documented feedback values.</summary>
public sealed class RateRoadmapItemRequestDtoValidatorTests
{
    private readonly RateRoadmapItemRequestDtoValidator _validator = new();

    // TEST-01: Accept the two documented values.
    /// <summary>Ensures both documented feedback values are accepted.</summary>
    [Theory]
    [InlineData("like")]
    [InlineData("dislike")]
    public void Validate_WithDocumentedValue_ShouldPass(string value)
    {
        _validator.Validate(new RateRoadmapItemRequestDto { Value = value }).IsValid.Should().BeTrue();
    }

    // TEST-02: Reject a missing, empty, unknown, or differently cased value on the value field.
    /// <summary>Ensures any other value is rejected on the value field.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("love")]
    [InlineData("Like")]
    public void Validate_WithInvalidValue_ShouldFailOnValue(string? value)
    {
        var result = _validator.Validate(new RateRoadmapItemRequestDto { Value = value! });

        result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be("Value");
    }
}
