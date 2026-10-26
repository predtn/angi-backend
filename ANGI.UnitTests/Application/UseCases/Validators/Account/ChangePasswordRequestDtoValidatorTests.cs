using ANGI.Application.DTOs.Account;
using ANGI.Application.UseCases.Validators.Account;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Account;

public sealed class ChangePasswordRequestDtoValidatorTests
{
    private readonly ChangePasswordRequestDtoValidator _validator = new();

    // TEST-01: Verify Validate with valid data; should be valid.
    /// <summary>Verify Validate with valid data; should be valid.</summary>
    [Fact]
    public void Validate_WithValidData_ShouldBeValid()
    {
        var dto = new ChangePasswordRequestDto
        {
            CurrentPassword = "OldPassword123",
            NewPassword = "NewPassword456"
        };

        var result = _validator.Validate(dto);

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Verify Validate with empty new password; should be invalid.
    /// <summary>Verify Validate with empty new password; should be invalid.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_WithEmptyNewPassword_ShouldBeInvalid(string? newPassword)
    {
        var dto = new ChangePasswordRequestDto
        {
            CurrentPassword = "OldPassword123",
            NewPassword = newPassword!
        };

        var result = _validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ChangePasswordRequestDto.NewPassword));
    }

    // TEST-03: Verify Validate with new password shorter than8 chars; should be invalid.
    /// <summary>Verify Validate with new password shorter than8 chars; should be invalid.</summary>
    [Fact]
    public void Validate_WithNewPasswordShorterThan8Chars_ShouldBeInvalid()
    {
        var dto = new ChangePasswordRequestDto
        {
            CurrentPassword = "OldPassword123",
            NewPassword = "Pass1"
        };

        var result = _validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("ít nhất 8 ký tự"));
    }

    // TEST-04: Verify Validate with new password longer than64 chars; should be invalid.
    /// <summary>Verify Validate with new password longer than64 chars; should be invalid.</summary>
    [Fact]
    public void Validate_WithNewPasswordLongerThan64Chars_ShouldBeInvalid()
    {
        var dto = new ChangePasswordRequestDto
        {
            CurrentPassword = "OldPassword123",
            NewPassword = new string('A', 64) + "1" // 65 chars
        };

        var result = _validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("vượt quá 64 ký tự"));
    }

    // TEST-05: Verify Validate with new password without digits; should be invalid.
    /// <summary>Verify Validate with new password without digits; should be invalid.</summary>
    [Fact]
    public void Validate_WithNewPasswordWithoutDigits_ShouldBeInvalid()
    {
        var dto = new ChangePasswordRequestDto
        {
            CurrentPassword = "OldPassword123",
            NewPassword = "PasswordOnlyLetters"
        };

        var result = _validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("ít nhất một chữ số"));
    }

    // TEST-06: Verify Validate with new password without letters; should be invalid.
    /// <summary>Verify Validate with new password without letters; should be invalid.</summary>
    [Fact]
    public void Validate_WithNewPasswordWithoutLetters_ShouldBeInvalid()
    {
        var dto = new ChangePasswordRequestDto
        {
            CurrentPassword = "OldPassword123",
            NewPassword = "123456789012"
        };

        var result = _validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("ít nhất một chữ cái"));
    }

    // TEST-07: Verify Validate when new password matches current password; should be invalid.
    /// <summary>Verify Validate when new password matches current password; should be invalid.</summary>
    [Fact]
    public void Validate_WhenNewPasswordMatchesCurrentPassword_ShouldBeInvalid()
    {
        var dto = new ChangePasswordRequestDto
        {
            CurrentPassword = "MatchingPassword123",
            NewPassword = "MatchingPassword123"
        };

        var result = _validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("không được trùng"));
    }

    // TEST-08: Verify Validate when new password matches old password alias; should be invalid.
    /// <summary>Verify Validate when new password matches old password alias; should be invalid.</summary>
    [Fact]
    public void Validate_WhenNewPasswordMatchesOldPasswordAlias_ShouldBeInvalid()
    {
        var dto = new ChangePasswordRequestDto
        {
            OldPassword = "MatchingPassword123",
            NewPassword = "MatchingPassword123"
        };

        var result = _validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("không được trùng"));
    }
}
