using ANGI.Application.DTOs.Account;
using ANGI.Application.UseCases.Validators.Account;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Account;

public sealed class ChangePasswordRequestDtoValidatorTests
{
    private readonly ChangePasswordRequestDtoValidator _validator = new();

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
