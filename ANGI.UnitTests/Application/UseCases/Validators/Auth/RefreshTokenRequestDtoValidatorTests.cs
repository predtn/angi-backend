using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Auth
{
    /// <summary>Verifies RefreshTokenRequestDtoValidator requires a non-blank refresh token.</summary>
    public sealed class RefreshTokenRequestDtoValidatorTests
    {
        private readonly RefreshTokenRequestDtoValidator _validator = new();

        // TEST-01: Accept a non-empty refresh token.
        /// <summary>Ensures a request with a token passes validation.</summary>
        [Fact]
        public void Validate_WithRefreshToken_ShouldHaveNoErrors()
        {
            var result = _validator.TestValidate(new RefreshTokenRequestDto { RefreshToken = "refresh-token" });

            result.ShouldNotHaveAnyValidationErrors();
        }

        // TEST-02: Reject an empty or blank refresh token.
        /// <summary>Ensures a blank token is rejected on the refreshToken field.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Validate_WithEmptyRefreshToken_ShouldFailOnRefreshToken(string refreshToken)
        {
            var result = _validator.TestValidate(new RefreshTokenRequestDto { RefreshToken = refreshToken });

            result.ShouldHaveValidationErrorFor(x => x.RefreshToken).WithErrorMessage("Refresh token là bắt buộc.");
        }
    }
}
