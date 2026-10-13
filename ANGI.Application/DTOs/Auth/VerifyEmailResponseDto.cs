namespace ANGI.Application.DTOs.Auth
{
    public sealed class VerifyEmailResponseDto
    {
        public int UserId { get; set; }
        public DateTime EmailVerifiedAt { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
