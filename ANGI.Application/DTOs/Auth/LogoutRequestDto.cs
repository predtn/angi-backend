namespace ANGI.Application.DTOs.Auth
{
    public sealed class LogoutRequestDto
    {
        public string RefreshToken { get; set; } = string.Empty;
    }
}
