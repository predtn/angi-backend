using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Validators.Auth;
using FluentValidation.TestHelper;

namespace ANGI.Test.Application.Validators.Auth
{
    public sealed class LoginRequestDtoValidatorTests
    {
        private readonly LoginRequestDtoValidator _validator = new();

        // TEST-01: Accept a well-formed email with a password.
        [Fact]
        public void Validate_WithValidRequest_ShouldHaveNoErrors()
        {
            var result = _validator.TestValidate(new LoginRequestDto { Email = "an.nguyen@gmail.com", Password = "MatKhau123" });

            result.ShouldNotHaveAnyValidationErrors();
        }

        // TEST-02: Reject a missing email.
        [Fact]
        public void Validate_WithEmptyEmail_ShouldFailOnEmail()
        {
            var result = _validator.TestValidate(new LoginRequestDto { Email = "", Password = "MatKhau123" });

            result.ShouldHaveValidationErrorFor(x => x.Email).WithErrorMessage("Email là bắt buộc.");
        }

        // TEST-03: Reject an email that is not an email address.
        [Fact]
        public void Validate_WithMalformedEmail_ShouldFailOnEmail()
        {
            var result = _validator.TestValidate(new LoginRequestDto { Email = "an.nguyen", Password = "MatKhau123" });

            result.ShouldHaveValidationErrorFor(x => x.Email).WithErrorMessage("Email không đúng định dạng.");
        }

        // TEST-04: Reject an email longer than users.email allows (255).
        [Fact]
        public void Validate_WithEmailOver255Characters_ShouldFailOnEmail()
        {
            var email = new string('a', 250) + "@gmail.com";

            var result = _validator.TestValidate(new LoginRequestDto { Email = email, Password = "MatKhau123" });

            result.ShouldHaveValidationErrorFor(x => x.Email).WithErrorMessage("Email không được vượt quá 255 ký tự.");
        }

        // TEST-05: Reject a missing password.
        [Fact]
        public void Validate_WithEmptyPassword_ShouldFailOnPassword()
        {
            var result = _validator.TestValidate(new LoginRequestDto { Email = "an.nguyen@gmail.com", Password = "" });

            result.ShouldHaveValidationErrorFor(x => x.Password).WithErrorMessage("Mật khẩu là bắt buộc.");
        }
    }
}
