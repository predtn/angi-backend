namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains the documented administrative user card without account secrets.</summary>
public class AdminUserCardDto
{
    public int Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? AvatarUrl { get; init; }
    public string Role { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime? SuspendedUntil { get; init; }
    public DateTime? LastLoginAt { get; init; }
    public DateTime CreatedAt { get; init; }
}
