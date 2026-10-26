using ANGI.Application.DTOs.Notification;
using ANGI.Application.UseCases.Validators.Notification;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Notification;

public sealed class MarkNotificationAsReadRequestDtoValidatorTests
{
    private readonly MarkNotificationAsReadRequestDtoValidator _validator = new();

    // TEST-01: Verify Validate with valid ids; should be valid.
    /// <summary>Verify Validate with valid ids; should be valid.</summary>
    [Fact]
    public void Validate_WithValidIds_ShouldBeValid()
    {
        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = new List<long> { 1, 2, 3 },
            All = false
        };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Verify Validate with all true and no ids; should be valid.
    /// <summary>Verify Validate with all true and no ids; should be valid.</summary>
    [Fact]
    public void Validate_WithAllTrueAndNoIds_ShouldBeValid()
    {
        var request = new MarkNotificationAsReadRequestDto
        {
            All = true
        };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeTrue();
    }

    // TEST-03: Verify Validate with neither ids nor all true; should be invalid.
    /// <summary>Verify Validate with neither ids nor all true; should be invalid.</summary>
    [Fact]
    public void Validate_WithNeitherIdsNorAllTrue_ShouldBeInvalid()
    {
        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = null,
            All = false
        };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ids");
    }

    // TEST-04: Verify Validate with empty ids and all false; should be invalid.
    /// <summary>Verify Validate with empty ids and all false; should be invalid.</summary>
    [Fact]
    public void Validate_WithEmptyIdsAndAllFalse_ShouldBeInvalid()
    {
        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = new List<long>(),
            All = false
        };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ids");
    }

    // TEST-05: Verify Validate with more than100 ids; should be invalid.
    /// <summary>Verify Validate with more than100 ids; should be invalid.</summary>
    [Fact]
    public void Validate_WithMoreThan100Ids_ShouldBeInvalid()
    {
        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = Enumerable.Range(1, 101).Select(i => (long)i).ToList(),
            All = false
        };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Ids");
    }

    // TEST-06: Verify Validate with zero or negative id; should be invalid.
    /// <summary>Verify Validate with zero or negative id; should be invalid.</summary>
    [Fact]
    public void Validate_WithZeroOrNegativeId_ShouldBeInvalid()
    {
        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = new List<long> { 1, 0, -5 },
            All = false
        };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.StartsWith("Ids["));
    }
}
