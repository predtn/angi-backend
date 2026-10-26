using ANGI.Application.DTOs.Account;
using ANGI.Application.UseCases.Validators.Account;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Account;

public sealed class UpdateProfileRequestDtoValidatorTests
{
    private readonly UpdateProfileRequestDtoValidator _validator = new();

    // TEST-01: Verify Validate with empty display name; should have validation error.
    /// <summary>Verify Validate with empty display name; should have validation error.</summary>
    [Fact]
    public void Validate_WithEmptyDisplayName_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { DisplayName = "   " };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.DisplayName));
    }

    // TEST-02: Verify Validate with short display name; should have validation error.
    /// <summary>Verify Validate with short display name; should have validation error.</summary>
    [Fact]
    public void Validate_WithShortDisplayName_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { DisplayName = "A" };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.DisplayName));
    }

    // TEST-03: Verify Validate with long display name; should have validation error.
    /// <summary>Verify Validate with long display name; should have validation error.</summary>
    [Fact]
    public void Validate_WithLongDisplayName_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { DisplayName = new string('A', 101) };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.DisplayName));
    }

    // TEST-04: Verify Validate with too long phone; should have validation error.
    /// <summary>Verify Validate with too long phone; should have validation error.</summary>
    [Fact]
    public void Validate_WithTooLongPhone_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { Phone = new string('1', 21) };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.Phone));
    }

    // TEST-05: Verify Validate with too long bio; should have validation error.
    /// <summary>Verify Validate with too long bio; should have validation error.</summary>
    [Fact]
    public void Validate_WithTooLongBio_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { Bio = new string('B', 501) };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.Bio));
    }

    // TEST-06: Verify Validate with negative avatar media id; should have validation error.
    /// <summary>Verify Validate with negative avatar media id; should have validation error.</summary>
    [Fact]
    public void Validate_WithNegativeAvatarMediaId_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { AvatarMediaId = -1 };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.AvatarMediaId));
    }

    // TEST-07: Verify Validate with valid fields; should pass validation.
    /// <summary>Verify Validate with valid fields; should pass validation.</summary>
    [Fact]
    public void Validate_WithValidFields_ShouldPassValidation()
    {
        var request = new UpdateProfileRequestDto
        {
            DisplayName = "An Nguyễn",
            Phone = "0905123456",
            Bio = "Mê ăn ngon",
            AvatarMediaId = 123
        };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeTrue();
    }
}
