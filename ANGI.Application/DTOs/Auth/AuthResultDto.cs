namespace ANGI.Application.DTOs.Auth
{
    public sealed class AuthResultDto
    {
        public string AccessToken { get; set; } = string.Empty;
        public DateTime AccessTokenExpiresAt { get; set; }
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime RefreshTokenExpiresAt { get; set; }
        public AuthUserDto User { get; set; } = null!;
    }
}
