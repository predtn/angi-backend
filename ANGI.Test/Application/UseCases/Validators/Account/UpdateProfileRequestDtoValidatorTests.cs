using ANGI.Application.DTOs.Account;
using ANGI.Application.UseCases.Validators.Account;
using FluentAssertions;

namespace ANGI.Test.Application.UseCases.Validators.Account;

public sealed class UpdateProfileRequestDtoValidatorTests
{
    private readonly UpdateProfileRequestDtoValidator _validator = new();

    [Fact]
    public void Validate_WithEmptyDisplayName_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { DisplayName = "   " };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.DisplayName));
    }

    [Fact]
    public void Validate_WithShortDisplayName_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { DisplayName = "A" };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.DisplayName));
    }

    [Fact]
    public void Validate_WithLongDisplayName_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { DisplayName = new string('A', 101) };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.DisplayName));
    }

    [Fact]
    public void Validate_WithTooLongPhone_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { Phone = new string('1', 21) };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.Phone));
    }

    [Fact]
    public void Validate_WithTooLongBio_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { Bio = new string('B', 501) };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.Bio));
    }

    [Fact]
    public void Validate_WithNegativeAvatarMediaId_ShouldHaveValidationError()
    {
        var request = new UpdateProfileRequestDto { AvatarMediaId = -1 };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateProfileRequestDto.AvatarMediaId));
    }

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
