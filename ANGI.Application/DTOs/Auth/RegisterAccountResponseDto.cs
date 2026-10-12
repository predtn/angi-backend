namespace ANGI.Application.DTOs.Auth;

/// <summary>Returns the newly created account fields documented by AUTH-01.</summary>
public sealed class RegisterAccountResponseDto
{
    public int UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
