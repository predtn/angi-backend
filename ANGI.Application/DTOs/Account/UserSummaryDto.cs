namespace ANGI.Application.DTOs.Account;

/// <summary>Represents the compact user identity embedded in other API DTOs.</summary>
public sealed class UserSummaryDto
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
}
