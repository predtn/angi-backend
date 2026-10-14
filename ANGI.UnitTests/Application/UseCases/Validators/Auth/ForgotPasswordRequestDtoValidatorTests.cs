using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Auth
{
    public sealed class ForgotPasswordRequestDtoValidatorTests
    {
        // TEST-01: Accept a valid password-reset email address.
        /// <summary>Verifies AUTH-08 accepts a syntactically valid email.</summary>
        [Fact]
        public void Validate_WithValidEmail_ShouldBeValid()
        {
            var result = new ForgotPasswordRequestDtoValidator().Validate(
                new ForgotPasswordRequestDto { Email = "user@angi.test" });

            result.IsValid.Should().BeTrue();
        }

        // TEST-02: Reject a malformed password-reset email address.
        /// <summary>Verifies AUTH-08 rejects invalid email syntax.</summary>
        [Fact]
        public void Validate_WithMalformedEmail_ShouldBeInvalid()
        {
            var result = new ForgotPasswordRequestDtoValidator().Validate(
                new ForgotPasswordRequestDto { Email = "not-an-email" });

            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(ForgotPasswordRequestDto.Email));
        }

        // TEST-03: Reject null, empty, and whitespace-only password-reset addresses.
        /// <summary>Verifies AUTH-08 treats every empty representation as a missing email.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Validate_WithMissingEmail_ShouldBeInvalid(string? email)
        {
            var result = new ForgotPasswordRequestDtoValidator().Validate(
                new ForgotPasswordRequestDto { Email = email! });

            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(ForgotPasswordRequestDto.Email));
        }

        // TEST-04: Accept the documented maximum email length and reject one character above it.
        /// <summary>Verifies the AUTH-08 email boundary is inclusive at 255 characters.</summary>
        [Theory]
        [InlineData(255, true)]
        [InlineData(256, false)]
        public void Validate_WithBoundaryLengthEmail_ShouldMatchMaximum(int length, bool expectedValid)
        {
            const string domain = "@example.com";
            var email = new string('a', length - domain.Length) + domain;

            var result = new ForgotPasswordRequestDtoValidator().Validate(
                new ForgotPasswordRequestDto { Email = email });

            result.IsValid.Should().Be(expectedValid);
        }

        // TEST-05: Accept outer whitespace because AUTH-08 normalizes pasted addresses before lookup.
        /// <summary>Verifies a valid address remains acceptable when surrounded by spaces.</summary>
        [Fact]
        public void Validate_WithOuterWhitespaceAroundValidEmail_ShouldBeValid()
        {
            var result = new ForgotPasswordRequestDtoValidator().Validate(
                new ForgotPasswordRequestDto { Email = "  user@angi.test  " });

            result.IsValid.Should().BeTrue();
        }
    }
}
