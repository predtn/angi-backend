namespace ANGI.Application.DTOs.Auth
{
    public sealed class GoogleLoginResponseDto : AuthResultDto
    {
        public bool IsNewUser { get; set; }
    }
}
