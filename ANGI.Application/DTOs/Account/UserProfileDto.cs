namespace ANGI.Application.DTOs.Account;

/// <summary>Represents the full personal profile of an authenticated user (ME-01, ME-02).</summary>
public sealed class UserProfileDto
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string? Phone { get; set; }
    public string? Bio { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool EmailVerified { get; set; }
    public bool HasPassword { get; set; }
    public IReadOnlyList<string> LinkedProviders { get; set; } = [];
    public string? PreferenceStatus { get; set; }
    public long? RestaurantId { get; set; }
    public IReadOnlyList<string> Permissions { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}
