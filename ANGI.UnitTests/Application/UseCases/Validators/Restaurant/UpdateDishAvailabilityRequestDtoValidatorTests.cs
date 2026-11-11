using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Verifies UpdateDishAvailabilityRequestDtoValidator requires an explicit availability value.</summary>
public sealed class UpdateDishAvailabilityRequestDtoValidatorTests
{
    // TEST-01: Reject an omitted availability value.
    /// <summary>Verifies OWN-10 requires isAvailable in the JSON body.</summary>
    [Fact]
    public async Task ValidateAsync_WithoutAvailability_ShouldRejectRequest()
    {
        // STEP-01: Validate an empty UpdateDishAvailabilityRequestDto and keep result.
        var result = await new UpdateDishAvailabilityRequestDtoValidator()
            .ValidateAsync(new UpdateDishAvailabilityRequestDto());

        // STEP-02: Assert result.Errors has exactly one matching item.
        result.Errors.Should().ContainSingle(error => error.PropertyName == "IsAvailable");
    }
}
