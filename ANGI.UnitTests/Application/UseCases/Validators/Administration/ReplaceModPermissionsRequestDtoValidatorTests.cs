using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Administration;

public sealed class ReplaceModPermissionsRequestDtoValidatorTests
{
    private readonly ReplaceModPermissionsRequestDtoValidator _validator = new();

    // TEST-01: Accept an empty list because it intentionally removes every override.
    /// <summary>Verifies the lower collection boundary defined by ADM-P3.</summary>
    [Fact]
    public async Task Validate_WithEmptyOverrides_ShouldBeValid()
    {
        var result = await _validator.ValidateAsync(new ReplaceModPermissionsRequestDto { Overrides = [] });

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Accept both documented override effects.
    /// <summary>Verifies allow and deny are the complete supported effect domain.</summary>
    [Fact]
    public async Task Validate_WithAllowAndDeny_ShouldBeValid()
    {
        var request = new ReplaceModPermissionsRequestDto
        {
            Overrides =
            [
                new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = "allow" },
                new PermissionOverrideDto { PermissionCode = "MENU_APPROVE", Effect = "deny" }
            ]
        };

        var result = await _validator.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }

    // TEST-03: Reject a null required overrides array exactly once.
    /// <summary>Verifies explicit JSON null reaches FluentValidation and is rejected.</summary>
    [Fact]
    public async Task Validate_WithNullOverrides_ShouldHaveRequiredError()
    {
        var result = await _validator.ValidateAsync(new ReplaceModPermissionsRequestDto { Overrides = null! });

        result.Errors.Should().ContainSingle(error => error.PropertyName == "Overrides");
    }

    // TEST-04: Reject missing nested fields without duplicating required messages.
    /// <summary>Verifies both non-null nested request properties are protected.</summary>
    [Fact]
    public async Task Validate_WithMissingNestedFields_ShouldRejectBothFieldsOnce()
    {
        var request = new ReplaceModPermissionsRequestDto { Overrides = [new PermissionOverrideDto()] };

        var result = await _validator.ValidateAsync(request);

        result.Errors.Should().ContainSingle(error => error.PropertyName == "Overrides[0].PermissionCode");
        result.Errors.Should().ContainSingle(error => error.PropertyName == "Overrides[0].Effect");
    }

    // TEST-05: Reject effect values outside the exact lowercase API enum.
    /// <summary>Verifies casing and unknown values cannot reach persistence.</summary>
    [Theory]
    [InlineData("ALLOW")]
    [InlineData("unknown")]
    [InlineData(" allow ")]
    public async Task Validate_WithUnsupportedEffect_ShouldBeInvalid(string effect)
    {
        var request = new ReplaceModPermissionsRequestDto
        {
            Overrides = [new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = effect }]
        };

        var result = await _validator.ValidateAsync(request);

        result.Errors.Should().ContainSingle(error => error.PropertyName == "Overrides[0].Effect");
    }

    // TEST-06: Reject duplicate permission codes before the composite database key can fail.
    /// <summary>Verifies one replacement payload cannot contain ambiguous duplicate overrides.</summary>
    [Fact]
    public async Task Validate_WithDuplicateCodes_ShouldBeInvalid()
    {
        var request = new ReplaceModPermissionsRequestDto
        {
            Overrides =
            [
                new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = "allow" },
                new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = "deny" }
            ]
        };

        var result = await _validator.ValidateAsync(request);

        result.Errors.Should().ContainSingle(error => error.PropertyName == "Overrides");
    }
}
