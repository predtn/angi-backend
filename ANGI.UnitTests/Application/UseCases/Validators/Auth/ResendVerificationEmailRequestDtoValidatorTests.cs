using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Auth
{
    /// <summary>Verifies ResendVerificationEmailRequestDtoValidator accepts only well-formed emails within the length limit.</summary>
    public sealed class ResendVerificationEmailRequestDtoValidatorTests
    {
        // TEST-01: Accept a valid email address for a verification resend.
        /// <summary>Verifies AUTH-03 accepts a syntactically valid email address.</summary>
        [Fact]
        public void Validate_WithValidEmail_ShouldBeValid()
        {
            // STEP-01: Validate ResendVerificationEmailRequestDto { Email = "user@angi.test" } and keep result.
            var result = new ResendVerificationEmailRequestDtoValidator().Validate(
                new ResendVerificationEmailRequestDto { Email = "user@angi.test" });

            // STEP-02: Assert result.IsValid is true.
            result.IsValid.Should().BeTrue();
        }

        // TEST-02: Reject a malformed resend email address.
        /// <summary>Verifies AUTH-03 rejects an invalid email before account lookup.</summary>
        [Fact]
        public void Validate_WithMalformedEmail_ShouldBeInvalid()
        {
            // STEP-01: Validate ResendVerificationEmailRequestDto { Email = "not-an-email" } and keep result.
            var result = new ResendVerificationEmailRequestDtoValidator().Validate(
                new ResendVerificationEmailRequestDto { Email = "not-an-email" });

            // STEP-02: Assert result.IsValid is false and result.Errors contains a matching item.
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(ResendVerificationEmailRequestDto.Email));
        }

        // TEST-03: Reject null, empty, and whitespace-only resend addresses.
        /// <summary>Verifies AUTH-03 treats every empty representation as a missing email.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Validate_WithMissingEmail_ShouldBeInvalid(string? email)
        {
            // STEP-01: Validate ResendVerificationEmailRequestDto { Email = email } and keep result.
            var result = new ResendVerificationEmailRequestDtoValidator().Validate(
                new ResendVerificationEmailRequestDto { Email = email! });

            // STEP-02: Assert result.IsValid is false and result.Errors contains a matching item.
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(ResendVerificationEmailRequestDto.Email));
        }

        // TEST-04: Accept the documented maximum email length and reject one character above it.
        /// <summary>Verifies the AUTH-03 email boundary is inclusive at 255 characters.</summary>
        [Theory]
        [InlineData(255, true)]
        [InlineData(256, false)]
        public void Validate_WithBoundaryLengthEmail_ShouldMatchMaximum(int length, bool expectedValid)
        {
            // STEP-01: Define domain as "@example.com" and derive email from Length) + domain.
            const string domain = "@example.com";
            var email = new string('a', length - domain.Length) + domain;

            // STEP-02: Validate ResendVerificationEmailRequestDto { Email = email } and keep result.
            var result = new ResendVerificationEmailRequestDtoValidator().Validate(
                new ResendVerificationEmailRequestDto { Email = email });

            // STEP-03: Assert result.IsValid is expectedValid.
            result.IsValid.Should().Be(expectedValid);
        }

        // TEST-05: Accept surrounding spaces because the use case normalizes them before lookup.
        /// <summary>Verifies a syntactically valid address remains acceptable when pasted with outer spaces.</summary>
        [Fact]
        public void Validate_WithOuterWhitespaceAroundValidEmail_ShouldBeValid()
        {
            // STEP-01: Validate ResendVerificationEmailRequestDto { Email = " user@angi.test " } and keep result.
            var result = new ResendVerificationEmailRequestDtoValidator().Validate(
                new ResendVerificationEmailRequestDto { Email = "  user@angi.test  " });

            // STEP-02: Assert result.IsValid is true.
            result.IsValid.Should().BeTrue();
        }
    }
}
