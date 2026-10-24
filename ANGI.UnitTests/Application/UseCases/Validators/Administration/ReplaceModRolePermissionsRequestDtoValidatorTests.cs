using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Administration;

public sealed class ReplaceModRolePermissionsRequestDtoValidatorTests
{
    private readonly ReplaceModRolePermissionsRequestDtoValidator _validator = new();

    // TEST-01: Accept an empty list because it intentionally removes every MOD role default.
    /// <summary>Verifies the lower collection boundary defined by ADM-P4.</summary>
    [Fact]
    public async Task Validate_WithEmptyPermissionCodes_ShouldBeValid()
    {
        var result = await _validator.ValidateAsync(
            new ReplaceModRolePermissionsRequestDto { PermissionCodes = [] });

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject an explicit null required permission-code array exactly once.
    /// <summary>Verifies explicit JSON null is rejected by FluentValidation.</summary>
    [Fact]
    public async Task Validate_WithNullPermissionCodes_ShouldHaveRequiredError()
    {
        var result = await _validator.ValidateAsync(
            new ReplaceModRolePermissionsRequestDto { PermissionCodes = null! });

        result.Errors.Should().ContainSingle(error => error.PropertyName == "PermissionCodes");
    }

    // TEST-03: Reject empty or whitespace permission-code elements.
    /// <summary>Verifies each array entry contains a usable catalog key.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Validate_WithBlankPermissionCode_ShouldBeInvalid(string code)
    {
        var result = await _validator.ValidateAsync(
            new ReplaceModRolePermissionsRequestDto { PermissionCodes = [code] });

        result.Errors.Should().ContainSingle(error => error.PropertyName == "PermissionCodes[0]");
    }

    // TEST-04: Reject duplicate codes before the composite database key can fail.
    /// <summary>Verifies the complete replacement set is unambiguous.</summary>
    [Fact]
    public async Task Validate_WithDuplicateCodes_ShouldBeInvalid()
    {
        var result = await _validator.ValidateAsync(new ReplaceModRolePermissionsRequestDto
        {
            PermissionCodes = ["USER_BAN", "USER_BAN"]
        });

        result.Errors.Should().ContainSingle(error => error.PropertyName == "PermissionCodes");
    }
}
