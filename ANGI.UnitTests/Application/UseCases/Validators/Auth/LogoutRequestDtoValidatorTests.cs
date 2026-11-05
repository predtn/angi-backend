using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Auth
{
    /// <summary>Verifies LogoutRequestDtoValidator requires a non-blank refresh token.</summary>
    public sealed class LogoutRequestDtoValidatorTests
    {
        private readonly LogoutRequestDtoValidator _validator = new();

        // TEST-01: Accept a non-empty refresh token.
        /// <summary>Ensures a request with a token passes validation.</summary>
        [Fact]
        public void Validate_WithRefreshToken_ShouldHaveNoErrors()
        {
            // STEP-01: Validate LogoutRequestDto { RefreshToken = a test value } and keep result.
            var result = _validator.TestValidate(new LogoutRequestDto { RefreshToken = "refresh-token" });

            // STEP-02: Assert no validation errors are reported.
            result.ShouldNotHaveAnyValidationErrors();
        }

        // TEST-02: Reject an empty or blank refresh token.
        /// <summary>Ensures a blank token is rejected on the refreshToken field.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Validate_WithEmptyRefreshToken_ShouldFailOnRefreshToken(string refreshToken)
        {
            // STEP-01: Validate LogoutRequestDto { RefreshToken = refreshToken } and keep result.
            var result = _validator.TestValidate(new LogoutRequestDto { RefreshToken = refreshToken });

            // STEP-02: Assert a validation error on RefreshToken with the expected message.
            result.ShouldHaveValidationErrorFor(x => x.RefreshToken).WithErrorMessage("Refresh token là bắt buộc.");
        }
    }
}
