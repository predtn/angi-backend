using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Administration;

/// <summary>Verifies ReplaceModRolePermissionsRequestDtoValidator checks the role default list for nulls, blanks and duplicates.</summary>
public sealed class ReplaceModRolePermissionsRequestDtoValidatorTests
{
    private readonly ReplaceModRolePermissionsRequestDtoValidator _validator = new();

    // TEST-01: Accept an empty list because it intentionally removes every MOD role default.
    /// <summary>Verifies the lower collection boundary defined by ADM-P4.</summary>
    [Fact]
    public async Task Validate_WithEmptyPermissionCodes_ShouldBeValid()
    {
        // STEP-01: Validate ReplaceModRolePermissionsRequestDto { PermissionCodes = the expected items } and keep result.
        var result = await _validator.ValidateAsync(
            new ReplaceModRolePermissionsRequestDto { PermissionCodes = [] });

        // STEP-02: Assert result.IsValid is true.
        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject an explicit null required permission-code array exactly once.
    /// <summary>Verifies explicit JSON null is rejected by FluentValidation.</summary>
    [Fact]
    public async Task Validate_WithNullPermissionCodes_ShouldHaveRequiredError()
    {
        // STEP-01: Validate ReplaceModRolePermissionsRequestDto { PermissionCodes = null } and keep result.
        var result = await _validator.ValidateAsync(
            new ReplaceModRolePermissionsRequestDto { PermissionCodes = null! });

        // STEP-02: Assert result.Errors has exactly one matching item.
        result.Errors.Should().ContainSingle(error => error.PropertyName == "PermissionCodes");
    }

    // TEST-03: Reject empty or whitespace permission-code elements.
    /// <summary>Verifies each array entry contains a usable catalog key.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Validate_WithBlankPermissionCode_ShouldBeInvalid(string code)
    {
        // STEP-01: Validate ReplaceModRolePermissionsRequestDto { PermissionCodes = the expected items } and keep result.
        var result = await _validator.ValidateAsync(
            new ReplaceModRolePermissionsRequestDto { PermissionCodes = [code] });

        // STEP-02: Assert result.Errors has exactly one matching item.
        result.Errors.Should().ContainSingle(error => error.PropertyName == "PermissionCodes[0]");
    }

    // TEST-04: Reject duplicate codes before the composite database key can fail.
    /// <summary>Verifies the complete replacement set is unambiguous.</summary>
    [Fact]
    public async Task Validate_WithDuplicateCodes_ShouldBeInvalid()
    {
        // STEP-01: Validate ReplaceModRolePermissionsRequestDto { PermissionCodes = the expected items } and keep result.
        var result = await _validator.ValidateAsync(new ReplaceModRolePermissionsRequestDto
        {
            PermissionCodes = ["USER_BAN", "USER_BAN"]
        });

        // STEP-02: Assert result.Errors has exactly one matching item.
        result.Errors.Should().ContainSingle(error => error.PropertyName == "PermissionCodes");
    }
}
