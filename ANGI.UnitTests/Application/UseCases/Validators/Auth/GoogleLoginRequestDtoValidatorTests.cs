using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Auth
{
    /// <summary>Verifies GoogleLoginRequestDtoValidator requires an ID token and allows only self-registration roles.</summary>
    public sealed class GoogleLoginRequestDtoValidatorTests
    {
        private readonly GoogleLoginRequestDtoValidator _validator = new();

        // TEST-01: Accept a token without a role because linked accounts do not send the registration role.
        /// <summary>Verifies role remains optional at DTO validation time for returning users.</summary>
        [Fact]
        public async Task ValidateAsync_WithTokenAndNoRole_ShouldBeValid()
        {
            // STEP-01: Validate GoogleLoginRequestDto { IdToken = a test value } and keep result.
            var result = await _validator.ValidateAsync(new GoogleLoginRequestDto { IdToken = "id-token" });

            // STEP-02: Assert result.IsValid is true.
            result.IsValid.Should().BeTrue();
        }

        // TEST-02: Accept both roles that guests may select when Google creates a new account.
        /// <summary>Verifies the two self-registration roles defined by AUTH-05 are accepted.</summary>
        [Theory]
        [InlineData("TRAVELER")]
        [InlineData("RESTAURANT_OWNER")]
        public async Task ValidateAsync_WithSupportedRole_ShouldBeValid(string role)
        {
            // STEP-01: Validate GoogleLoginRequestDto { IdToken = a test value, Role = role } and keep result.
            var result = await _validator.ValidateAsync(new GoogleLoginRequestDto
            {
                IdToken = "id-token",
                Role = role
            });

            // STEP-02: Assert result.IsValid is true.
            result.IsValid.Should().BeTrue();
        }

        // TEST-03: Reject an empty Google ID token before any external verification occurs.
        /// <summary>Verifies null-equivalent token input fails the required-field rule.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ValidateAsync_WithEmptyToken_ShouldBeInvalid(string idToken)
        {
            // STEP-01: Validate GoogleLoginRequestDto { IdToken = idToken } and keep result.
            var result = await _validator.ValidateAsync(new GoogleLoginRequestDto { IdToken = idToken });

            // STEP-02: Assert result.IsValid is false and result.Errors contains a matching item.
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(GoogleLoginRequestDto.IdToken));
        }

        // TEST-04: Reject privileged, unknown, and blank registration roles.
        /// <summary>Verifies callers cannot self-register privileged or undocumented roles.</summary>
        [Theory]
        [InlineData("ADMIN")]
        [InlineData("MOD")]
        [InlineData("traveler")]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ValidateAsync_WithUnsupportedRole_ShouldBeInvalid(string role)
        {
            // STEP-01: Validate GoogleLoginRequestDto { IdToken = a test value, Role = role } and keep result.
            var result = await _validator.ValidateAsync(new GoogleLoginRequestDto
            {
                IdToken = "id-token",
                Role = role
            });

            // STEP-02: Assert result.IsValid is false and result.Errors contains a matching item.
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.PropertyName == nameof(GoogleLoginRequestDto.Role));
        }
    }
}
