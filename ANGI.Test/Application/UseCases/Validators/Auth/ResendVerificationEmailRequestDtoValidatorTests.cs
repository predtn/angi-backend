using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentAssertions;

namespace ANGI.Test.Application.UseCases.Validators.Auth
{
    public sealed class ResendVerificationEmailRequestDtoValidatorTests
    {
        // TEST-01: Accept a valid email address for a verification resend.
        /// <summary>Verifies AUTH-03 accepts a syntactically valid email address.</summary>
        [Fact]
        public void Validate_WithValidEmail_ShouldBeValid()
        {
            var result = new ResendVerificationEmailRequestDtoValidator().Validate(
                new ResendVerificationEmailRequestDto { Email = "user@angi.test" });

            result.IsValid.Should().BeTrue();
        }

        // TEST-02: Reject a malformed resend email address.
        /// <summary>Verifies AUTH-03 rejects an invalid email before account lookup.</summary>
        [Fact]
        public void Validate_WithMalformedEmail_ShouldBeInvalid()
        {
            var result = new ResendVerificationEmailRequestDtoValidator().Validate(
                new ResendVerificationEmailRequestDto { Email = "not-an-email" });

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
            var result = new ResendVerificationEmailRequestDtoValidator().Validate(
                new ResendVerificationEmailRequestDto { Email = email! });

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
            const string domain = "@example.com";
            var email = new string('a', length - domain.Length) + domain;

            var result = new ResendVerificationEmailRequestDtoValidator().Validate(
                new ResendVerificationEmailRequestDto { Email = email });

            result.IsValid.Should().Be(expectedValid);
        }

        // TEST-05: Accept surrounding spaces because the use case normalizes them before lookup.
        /// <summary>Verifies a syntactically valid address remains acceptable when pasted with outer spaces.</summary>
        [Fact]
        public void Validate_WithOuterWhitespaceAroundValidEmail_ShouldBeValid()
        {
            var result = new ResendVerificationEmailRequestDtoValidator().Validate(
                new ResendVerificationEmailRequestDto { Email = "  user@angi.test  " });

            result.IsValid.Should().BeTrue();
        }
    }
}
