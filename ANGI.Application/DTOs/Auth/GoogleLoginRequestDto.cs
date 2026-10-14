namespace ANGI.Application.DTOs.Auth
{
    public sealed class GoogleLoginRequestDto
    {
        public string IdToken { get; set; } = string.Empty;
        public string? Role { get; set; }
    }
}
