using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentValidation.TestHelper;

namespace ANGI.Test.Application.UseCases.Validators.Auth
{
    public sealed class LogoutRequestDtoValidatorTests
    {
        private readonly LogoutRequestDtoValidator _validator = new();

        // TEST-01: Accept a non-empty refresh token.
        [Fact]
        public void Validate_WithRefreshToken_ShouldHaveNoErrors()
        {
            var result = _validator.TestValidate(new LogoutRequestDto { RefreshToken = "refresh-token" });

            result.ShouldNotHaveAnyValidationErrors();
        }

        // TEST-02: Reject an empty or blank refresh token.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Validate_WithEmptyRefreshToken_ShouldFailOnRefreshToken(string refreshToken)
        {
            var result = _validator.TestValidate(new LogoutRequestDto { RefreshToken = refreshToken });

            result.ShouldHaveValidationErrorFor(x => x.RefreshToken).WithErrorMessage("Refresh token là bắt buộc.");
        }
    }
}
