using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Auth
{
    /// <summary>Verifies LoginRequestDtoValidator requires a well-formed email and a password.</summary>
    public sealed class LoginRequestDtoValidatorTests
    {
        private readonly LoginRequestDtoValidator _validator = new();

        // TEST-01: Accept a well-formed email with a password.
        /// <summary>Ensures a normal login request is not blocked by validation.</summary>
        [Fact]
        public void Validate_WithValidRequest_ShouldHaveNoErrors()
        {
            var result = _validator.TestValidate(new LoginRequestDto { Email = "an.nguyen@gmail.com", Password = "MatKhau123" });

            result.ShouldNotHaveAnyValidationErrors();
        }

        // TEST-02: Reject a missing email.
        /// <summary>Ensures the error is reported on the email field when it is missing.</summary>
        [Fact]
        public void Validate_WithEmptyEmail_ShouldFailOnEmail()
        {
            var result = _validator.TestValidate(new LoginRequestDto { Email = "", Password = "MatKhau123" });

            result.ShouldHaveValidationErrorFor(x => x.Email).WithErrorMessage("Email là bắt buộc.");
        }

        // TEST-03: Reject an email that is not an email address.
        /// <summary>Ensures a value that is not an email is rejected on the email field.</summary>
        [Fact]
        public void Validate_WithMalformedEmail_ShouldFailOnEmail()
        {
            var result = _validator.TestValidate(new LoginRequestDto { Email = "an.nguyen", Password = "MatKhau123" });

            result.ShouldHaveValidationErrorFor(x => x.Email).WithErrorMessage("Email không đúng định dạng.");
        }

        // TEST-04: Reject an email longer than users.email allows (255).
        /// <summary>Ensures an email longer than the column allows is rejected before querying.</summary>
        [Fact]
        public void Validate_WithEmailOver255Characters_ShouldFailOnEmail()
        {
            var email = new string('a', 250) + "@gmail.com";

            var result = _validator.TestValidate(new LoginRequestDto { Email = email, Password = "MatKhau123" });

            result.ShouldHaveValidationErrorFor(x => x.Email).WithErrorMessage("Email không được vượt quá 255 ký tự.");
        }

        // TEST-05: Reject a missing password.
        /// <summary>Ensures the error is reported on the password field when it is missing.</summary>
        [Fact]
        public void Validate_WithEmptyPassword_ShouldFailOnPassword()
        {
            var result = _validator.TestValidate(new LoginRequestDto { Email = "an.nguyen@gmail.com", Password = "" });

            result.ShouldHaveValidationErrorFor(x => x.Password).WithErrorMessage("Mật khẩu là bắt buộc.");
        }
    }
}
