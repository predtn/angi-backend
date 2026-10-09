namespace ANGI.Application.DTOs.Auth;

/// <summary>Represents the AUTH-01 account registration payload.</summary>
public sealed class RegisterAccountRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}
