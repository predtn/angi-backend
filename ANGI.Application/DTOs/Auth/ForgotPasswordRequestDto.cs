namespace ANGI.Application.DTOs.Auth
{
    /// <summary>Contains the email address that should receive an AUTH-08 password-reset link.</summary>
    public sealed class ForgotPasswordRequestDto
    {
        public string Email { get; set; } = string.Empty;
    }
}
