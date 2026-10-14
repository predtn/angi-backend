using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

public sealed class UpdateDishAvailabilityRequestDtoValidatorTests
{
    // TEST-01: Reject an omitted availability value.
    /// <summary>Verifies OWN-10 requires isAvailable in the JSON body.</summary>
    [Fact]
    public async Task ValidateAsync_WithoutAvailability_ShouldRejectRequest()
    {
        var result = await new UpdateDishAvailabilityRequestDtoValidator()
            .ValidateAsync(new UpdateDishAvailabilityRequestDto());

        result.Errors.Should().ContainSingle(error => error.PropertyName == "IsAvailable");
    }
}
