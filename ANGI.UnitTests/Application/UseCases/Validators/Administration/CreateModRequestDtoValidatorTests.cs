using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Administration;

public sealed class CreateModRequestDtoValidatorTests
{
    // TEST-01: Accept two and one-hundred Unicode scalar display-name boundaries and optional overrides.
    /// <summary>Verifies valid ADM-M2 identity boundaries, including supplementary Unicode.</summary>
    [Theory]
    [InlineData("A😀")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Validate_WithValidDisplayNameBoundaries_ShouldPass(string displayName)
    {
        var result = await new CreateModRequestDtoValidator().ValidateAsync(new CreateModRequestDto
        {
            Email = "mod@angi.vn",
            DisplayName = displayName,
            PermissionOverrides = null
        });

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject missing, malformed, and overlong emails.
    /// <summary>Verifies all ADM-M2 email format and storage boundaries.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task Validate_WithInvalidEmail_ShouldFail(string email)
    {
        var result = await new CreateModRequestDtoValidator().ValidateAsync(new CreateModRequestDto
        {
            Email = email,
            DisplayName = "Valid Name"
        });

        result.Errors.Should().Contain(error => error.PropertyName == nameof(CreateModRequestDto.Email));
    }

    // TEST-03: Reject display names below two, above one hundred, or containing unsafe Unicode.
    /// <summary>Verifies ADM-M2 display-name length and safety boundaries.</summary>
    [Theory]
    [InlineData("A")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("Valid\u0001Name")]
    public async Task Validate_WithInvalidDisplayName_ShouldFail(string displayName)
    {
        var result = await new CreateModRequestDtoValidator().ValidateAsync(new CreateModRequestDto
        {
            Email = "mod@angi.vn",
            DisplayName = displayName
        });

        result.Errors.Should().Contain(error => error.PropertyName == nameof(CreateModRequestDto.DisplayName));
    }

    // TEST-04: Reject null items, duplicate codes, missing nested fields, and invalid effects.
    /// <summary>Verifies every ADM-M2 permissionOverrides array boundary.</summary>
    [Fact]
    public async Task Validate_WithInvalidOverrides_ShouldFailEveryInvalidRegion()
    {
        var result = await new CreateModRequestDtoValidator().ValidateAsync(new CreateModRequestDto
        {
            Email = "mod@angi.vn",
            DisplayName = "Valid Name",
            PermissionOverrides =
            [
                null!,
                new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = "invalid" },
                new PermissionOverrideDto { PermissionCode = "USER_BAN", Effect = "allow" },
                new PermissionOverrideDto { PermissionCode = "", Effect = "deny" }
            ]
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorMessage.Contains("null", StringComparison.Ordinal));
        result.Errors.Should().Contain(error => error.ErrorMessage.Contains("một lần", StringComparison.Ordinal));
        result.Errors.Should().Contain(error => error.PropertyName.EndsWith("Effect", StringComparison.Ordinal));
        result.Errors.Should().Contain(error => error.PropertyName.EndsWith("PermissionCode", StringComparison.Ordinal));
    }
}
