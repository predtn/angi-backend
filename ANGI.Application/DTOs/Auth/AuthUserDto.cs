namespace ANGI.Application.DTOs.Auth
{
    public sealed class AuthUserDto
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? AvatarUrl { get; set; }
        public string Role { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool? NeedsPreferenceSurvey { get; set; }
    }
}
