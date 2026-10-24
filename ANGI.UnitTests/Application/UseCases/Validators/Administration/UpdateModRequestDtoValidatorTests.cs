using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Administration;

public sealed class UpdateModRequestDtoValidatorTests
{
    // TEST-01: Reject an empty ADM-M4 patch and invalid status values.
    /// <summary>Verifies ADM-M4 requires at least one valid mutable field.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ACTIVE")]
    [InlineData("banned")]
    public async Task Validate_WithoutValidField_ShouldFail(string? status)
    {
        var result = await new UpdateModRequestDtoValidator().ValidateAsync(new UpdateModRequestDto { Status = status });

        result.IsValid.Should().BeFalse();
    }

    // TEST-02: Apply the same display-name boundaries to ADM-M4.
    /// <summary>Verifies invalid optional display names cannot bypass creation rules.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("Bad\u0001Name")]
    public async Task Validate_WithInvalidDisplayName_ShouldFail(string displayName)
    {
        var result = await new UpdateModRequestDtoValidator().ValidateAsync(new UpdateModRequestDto
        {
            DisplayName = displayName
        });

        result.Errors.Should().Contain(error => error.PropertyName == nameof(UpdateModRequestDto.DisplayName));
    }

    // TEST-03: Accept each ADM-M4 field independently and together.
    /// <summary>Verifies valid partial-update combinations at their boundaries.</summary>
    [Theory]
    [InlineData("AB", null)]
    [InlineData(null, "active")]
    [InlineData(null, "deactivated")]
    [InlineData("Valid Name", "deactivated")]
    public async Task Validate_WithValidPartialPatch_ShouldPass(string? displayName, string? status)
    {
        var result = await new UpdateModRequestDtoValidator().ValidateAsync(new UpdateModRequestDto
        {
            DisplayName = displayName,
            Status = status
        });

        result.IsValid.Should().BeTrue();
    }
}
