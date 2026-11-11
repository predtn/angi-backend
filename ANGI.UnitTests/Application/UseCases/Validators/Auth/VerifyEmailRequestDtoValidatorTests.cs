using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Auth
{
    /// <summary>Verifies VerifyEmailRequestDtoValidator requires a non-blank verification token.</summary>
    public sealed class VerifyEmailRequestDtoValidatorTests
    {
        // TEST-01: Accept a non-empty email verification token.
        /// <summary>Verifies AUTH-02 accepts the opaque token supplied by the email link.</summary>
        [Fact]
        public void Validate_WithToken_ShouldBeValid()
        {
            // STEP-01: Validate VerifyEmailRequestDto { Token = a test value } and keep result.
            var result = new VerifyEmailRequestDtoValidator().Validate(
                new VerifyEmailRequestDto { Token = "opaque-token" });

            // STEP-02: Assert result.IsValid is true.
            result.IsValid.Should().BeTrue();
        }

        // TEST-02: Reject an empty email verification token.
        /// <summary>Verifies AUTH-02 requires a token value.</summary>
        [Fact]
        public void Validate_WithEmptyToken_ShouldBeInvalid()
        {
            // STEP-01: Validate an empty VerifyEmailRequestDto and keep result.
            var result = new VerifyEmailRequestDtoValidator().Validate(new VerifyEmailRequestDto());

            // STEP-02: Assert result.IsValid is false and result.Errors contains a matching item.
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(VerifyEmailRequestDto.Token));
        }

        // TEST-03: Reject null, empty, and whitespace-only verification tokens.
        /// <summary>Verifies all representations of a missing AUTH-02 token fail validation.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Validate_WithMissingToken_ShouldBeInvalid(string? token)
        {
            // STEP-01: Validate VerifyEmailRequestDto { Token = token } and keep result.
            var result = new VerifyEmailRequestDtoValidator().Validate(
                new VerifyEmailRequestDto { Token = token! });

            // STEP-02: Assert result.IsValid is false and result.Errors contains a matching item.
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(VerifyEmailRequestDto.Token));
        }
    }
}
