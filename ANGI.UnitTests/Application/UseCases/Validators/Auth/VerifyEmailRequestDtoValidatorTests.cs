using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Auth
{
    public sealed class VerifyEmailRequestDtoValidatorTests
    {
        // TEST-01: Accept a non-empty email verification token.
        /// <summary>Verifies AUTH-02 accepts the opaque token supplied by the email link.</summary>
        [Fact]
        public void Validate_WithToken_ShouldBeValid()
        {
            var result = new VerifyEmailRequestDtoValidator().Validate(
                new VerifyEmailRequestDto { Token = "opaque-token" });

            result.IsValid.Should().BeTrue();
        }

        // TEST-02: Reject an empty email verification token.
        /// <summary>Verifies AUTH-02 requires a token value.</summary>
        [Fact]
        public void Validate_WithEmptyToken_ShouldBeInvalid()
        {
            var result = new VerifyEmailRequestDtoValidator().Validate(new VerifyEmailRequestDto());

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
            var result = new VerifyEmailRequestDtoValidator().Validate(
                new VerifyEmailRequestDto { Token = token! });

            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(VerifyEmailRequestDto.Token));
        }
    }
}
