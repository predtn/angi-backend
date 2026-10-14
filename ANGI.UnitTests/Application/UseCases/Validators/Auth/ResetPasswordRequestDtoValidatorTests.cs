using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Auth
{
    public sealed class ResetPasswordRequestDtoValidatorTests
    {
        // TEST-01: Accept passwords at both documented length boundaries when they contain a letter and digit.
        /// <summary>Verifies AUTH-09 includes both 8 and 64 characters in its valid password range.</summary>
        [Theory]
        [InlineData(8)]
        [InlineData(64)]
        public void Validate_WithBoundaryLengthPassword_ShouldBeValid(int length)
        {
            var password = "A1" + new string('x', length - 2);

            var result = Validator().Validate(ValidRequest(password));

            result.IsValid.Should().BeTrue();
        }

        // TEST-02: Reject passwords immediately outside both documented length boundaries.
        /// <summary>Verifies AUTH-09 rejects 7-character and 65-character passwords.</summary>
        [Theory]
        [InlineData(7)]
        [InlineData(65)]
        public void Validate_WithOutOfRangePasswordLength_ShouldBeInvalid(int length)
        {
            var password = "A1" + new string('x', length - 2);

            var result = Validator().Validate(ValidRequest(password));

            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(ResetPasswordRequestDto.NewPassword));
        }

        // TEST-03: Reject null, empty, and whitespace-only reset tokens.
        /// <summary>Verifies AUTH-09 requires a meaningful reset token.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Validate_WithMissingToken_ShouldBeInvalid(string? token)
        {
            var request = ValidRequest();
            request.Token = token!;

            var result = Validator().Validate(request);

            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(ResetPasswordRequestDto.Token));
        }

        // TEST-04: Reject null, empty, and whitespace-only replacement passwords.
        /// <summary>Verifies AUTH-09 cannot accept an absent replacement password.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Validate_WithMissingPassword_ShouldBeInvalid(string? password)
        {
            var result = Validator().Validate(ValidRequest(password!));

            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(ResetPasswordRequestDto.NewPassword));
        }

        // TEST-05: Reject a replacement password that has digits but no letters.
        /// <summary>Verifies the AUTH-09 password composition rule requires at least one letter.</summary>
        [Fact]
        public void Validate_WithDigitsOnlyPassword_ShouldBeInvalid()
        {
            var result = Validator().Validate(ValidRequest("12345678"));

            result.IsValid.Should().BeFalse();
        }

        // TEST-06: Reject a replacement password that has letters but no digits.
        /// <summary>Verifies the AUTH-09 password composition rule requires at least one digit.</summary>
        [Fact]
        public void Validate_WithLettersOnlyPassword_ShouldBeInvalid()
        {
            var result = Validator().Validate(ValidRequest("Password"));

            result.IsValid.Should().BeFalse();
        }

        /// <summary>Creates the validator under test.</summary>
        private static ResetPasswordRequestDtoValidator Validator() => new();

        /// <summary>Creates a valid AUTH-09 request with an optionally replaced password.</summary>
        private static ResetPasswordRequestDto ValidRequest(string password = "Password1") => new()
        {
            Token = "raw-token",
            NewPassword = password
        };
    }
}
