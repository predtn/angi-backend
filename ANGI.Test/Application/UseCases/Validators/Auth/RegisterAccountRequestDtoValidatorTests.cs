using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentValidation.TestHelper;

namespace ANGI.Test.Application.UseCases.Validators.Auth;

public sealed class RegisterAccountRequestDtoValidatorTests
{
    private readonly RegisterAccountRequestDtoValidator _validator = new();

    // TEST-01: Accept each role that AUTH-01 permits for self-registration.
    /// <summary>Verifies both documented self-registration roles are accepted.</summary>
    [Theory]
    [InlineData("TRAVELER")]
    [InlineData("RESTAURANT_OWNER")]
    public void Validate_WithAllowedRole_ShouldHaveNoErrors(string role)
    {
        var result = _validator.TestValidate(ValidRequest(role));

        result.ShouldNotHaveAnyValidationErrors();
    }

    // TEST-02: Reject MOD, ADMIN, and unknown roles as validation failures.
    /// <summary>Verifies privileged, differently-cased, and unknown roles are rejected.</summary>
    [Theory]
    [InlineData("MOD")]
    [InlineData("ADMIN")]
    [InlineData("traveler")]
    [InlineData("UNKNOWN")]
    public void Validate_WithDisallowedRole_ShouldFailOnRole(string role)
    {
        var result = _validator.TestValidate(ValidRequest(role));

        result.ShouldHaveValidationErrorFor(x => x.Role);
    }

    // TEST-03: Reject passwords outside 8 to 64 characters or without both a letter and a digit.
    /// <summary>Verifies all documented password constraints are enforced.</summary>
    [Theory]
    [InlineData("Short1")]
    [InlineData("onlyletters")]
    [InlineData("12345678")]
    public void Validate_WithInvalidPassword_ShouldFailOnPassword(string password)
    {
        var request = ValidRequest("TRAVELER");
        request.Password = password;

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Password);
    }

    // TEST-04: Reject malformed or oversized email and display-name values.
    /// <summary>Verifies malformed profile fields produce field-level validation failures.</summary>
    [Fact]
    public void Validate_WithInvalidProfileFields_ShouldFailOnTheirFields()
    {
        var request = ValidRequest("TRAVELER");
        request.Email = "not-an-email";
        request.DisplayName = " ";

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Email);
        result.ShouldHaveValidationErrorFor(x => x.DisplayName);
    }

    // TEST-05: Reject null, empty, and whitespace-only values for every required field.
    [Fact]
    public void Validate_WithMissingRequiredFields_ShouldFailOnEveryField()
    {
        var request = new RegisterAccountRequestDto
        {
            Email = null!,
            Password = string.Empty,
            DisplayName = "   ",
            Role = null!
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Email);
        result.ShouldHaveValidationErrorFor(x => x.Password);
        result.ShouldHaveValidationErrorFor(x => x.DisplayName);
        result.ShouldHaveValidationErrorFor(x => x.Role);
    }

    // TEST-06: Exercise both sides of the documented email maximum length.
    [Theory]
    [InlineData(255, false)]
    [InlineData(256, true)]
    public void Validate_WithEmailLengthBoundary_ShouldReturnExpectedResult(int length, bool shouldFail)
    {
        const string suffix = "@example.com";
        var request = ValidRequest("TRAVELER");
        request.Email = new string('a', length - suffix.Length) + suffix;

        var result = _validator.TestValidate(request);

        if (shouldFail)
        {
            result.ShouldHaveValidationErrorFor(x => x.Email);
        }
        else
        {
            result.ShouldNotHaveValidationErrorFor(x => x.Email);
        }
    }

    // TEST-07: Exercise the minimum and maximum password boundaries.
    [Theory]
    [InlineData(7, true)]
    [InlineData(8, false)]
    [InlineData(64, false)]
    [InlineData(65, true)]
    public void Validate_WithPasswordLengthBoundary_ShouldReturnExpectedResult(int length, bool shouldFail)
    {
        var request = ValidRequest("TRAVELER");
        request.Password = "A1" + new string('a', length - 2);

        var result = _validator.TestValidate(request);

        if (shouldFail)
        {
            result.ShouldHaveValidationErrorFor(x => x.Password);
        }
        else
        {
            result.ShouldNotHaveValidationErrorFor(x => x.Password);
        }
    }

    // TEST-08: Count display-name length after trimming, including both accepted boundaries.
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void Validate_WithDisplayNameLengthBoundary_ShouldReturnExpectedResult(int trimmedLength, bool shouldFail)
    {
        var request = ValidRequest("TRAVELER");
        request.DisplayName = " " + new string('A', trimmedLength) + " ";

        var result = _validator.TestValidate(request);

        if (shouldFail)
        {
            result.ShouldHaveValidationErrorFor(x => x.DisplayName);
        }
        else
        {
            result.ShouldNotHaveValidationErrorFor(x => x.DisplayName);
        }
    }

    // TEST-09: Block invisible controls, bidi formatting, private-use, replacement, and malformed UTF-16.
    [Theory]
    [InlineData("Nguyễn\nAn")]
    [InlineData("Nguyễn\u200BAn")]
    [InlineData("Nguyễn\u202EAn")]
    [InlineData("Nguyễn\uE000An")]
    [InlineData("Nguyễn\uFFFDAn")]
    [InlineData("Nguyễn\uD800An")]
    [InlineData("Nguyễn\u2028An")]
    public void Validate_WithUnsafeUnicodeInDisplayName_ShouldFail(string displayName)
    {
        var request = ValidRequest("TRAVELER");
        request.DisplayName = displayName;

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DisplayName);
    }

    // TEST-10: Preserve normal international names and valid supplementary Unicode.
    [Theory]
    [InlineData("Nguyễn An")]
    [InlineData("O'Connor-Smith")]
    [InlineData("An 🍜")]
    public void Validate_WithSafeUnicodeDisplayName_ShouldPass(string displayName)
    {
        var request = ValidRequest("TRAVELER");
        request.DisplayName = displayName;

        var result = _validator.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.DisplayName);
    }

    // TEST-11: Supplementary Unicode counts as one database character, not two UTF-16 code units.
    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void Validate_WithSupplementaryUnicodeLengthBoundary_ShouldReturnExpectedResult(
        int characterCount,
        bool shouldFail)
    {
        var request = ValidRequest("TRAVELER");
        request.DisplayName = string.Concat(Enumerable.Repeat("🍜", characterCount));

        var result = _validator.TestValidate(request);

        if (shouldFail)
        {
            result.ShouldHaveValidationErrorFor(x => x.DisplayName);
        }
        else
        {
            result.ShouldNotHaveValidationErrorFor(x => x.DisplayName);
        }
    }

    /// <summary>Creates a valid AUTH-01 request for validator tests.</summary>
    private static RegisterAccountRequestDto ValidRequest(string role) => new()
    {
        Email = "an.nguyen@gmail.com",
        Password = "MatKhau123",
        DisplayName = "Nguyễn An",
        Role = role
    };
}
