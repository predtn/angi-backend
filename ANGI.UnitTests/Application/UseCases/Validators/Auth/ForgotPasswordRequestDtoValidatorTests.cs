using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Auth
{
    /// <summary>Verifies ForgotPasswordRequestDtoValidator accepts only well-formed emails within the length limit.</summary>
    public sealed class ForgotPasswordRequestDtoValidatorTests
    {
        // TEST-01: Accept a valid password-reset email address.
        /// <summary>Verifies AUTH-08 accepts a syntactically valid email.</summary>
        [Fact]
        public void Validate_WithValidEmail_ShouldBeValid()
        {
            // STEP-01: Validate ForgotPasswordRequestDto { Email = "user@angi.test" } and keep result.
            var result = new ForgotPasswordRequestDtoValidator().Validate(
                new ForgotPasswordRequestDto { Email = "user@angi.test" });

            // STEP-02: Assert result.IsValid is true.
            result.IsValid.Should().BeTrue();
        }

        // TEST-02: Reject a malformed password-reset email address.
        /// <summary>Verifies AUTH-08 rejects invalid email syntax.</summary>
        [Fact]
        public void Validate_WithMalformedEmail_ShouldBeInvalid()
        {
            // STEP-01: Validate ForgotPasswordRequestDto { Email = "not-an-email" } and keep result.
            var result = new ForgotPasswordRequestDtoValidator().Validate(
                new ForgotPasswordRequestDto { Email = "not-an-email" });

            // STEP-02: Assert result.IsValid is false and result.Errors contains a matching item.
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
            // STEP-01: Validate ForgotPasswordRequestDto { Email = email } and keep result.
            var result = new ForgotPasswordRequestDtoValidator().Validate(
                new ForgotPasswordRequestDto { Email = email! });

            // STEP-02: Assert result.IsValid is false and result.Errors contains a matching item.
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
            // STEP-01: Define domain as "@example.com" and derive email from Length) + domain.
            const string domain = "@example.com";
            var email = new string('a', length - domain.Length) + domain;

            // STEP-02: Validate ForgotPasswordRequestDto { Email = email } and keep result.
            var result = new ForgotPasswordRequestDtoValidator().Validate(
                new ForgotPasswordRequestDto { Email = email });

            // STEP-03: Assert result.IsValid is expectedValid.
            result.IsValid.Should().Be(expectedValid);
        }

        // TEST-05: Accept outer whitespace because AUTH-08 normalizes pasted addresses before lookup.
        /// <summary>Verifies a valid address remains acceptable when surrounded by spaces.</summary>
        [Fact]
        public void Validate_WithOuterWhitespaceAroundValidEmail_ShouldBeValid()
        {
            // STEP-01: Validate ForgotPasswordRequestDto { Email = " user@angi.test " } and keep result.
            var result = new ForgotPasswordRequestDtoValidator().Validate(
                new ForgotPasswordRequestDto { Email = "  user@angi.test  " });

            // STEP-02: Assert result.IsValid is true.
            result.IsValid.Should().BeTrue();
        }
    }
}
