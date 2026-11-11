namespace ANGI.Application.DTOs.Auth
{
    /// <summary>Contains the one-time token and replacement password for AUTH-09.</summary>
    public sealed class ResetPasswordRequestDto
    {
        public string Token { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }
}
