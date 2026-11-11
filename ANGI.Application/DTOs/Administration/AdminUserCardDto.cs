namespace ANGI.Application.DTOs.Administration;

/// <summary>Represents a user account in administration account-management responses.</summary>
public class AdminUserCardDto
{
    /// <summary>Gets or sets the user identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the normalized account email.</summary>
    public string Email { get; set; } = null!;

    /// <summary>Gets or sets the account display name.</summary>
    public string DisplayName { get; set; } = null!;

    /// <summary>Gets or sets the public avatar URL when one exists.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>Gets or sets the stable uppercase role code.</summary>
    public string Role { get; set; } = null!;

    /// <summary>Gets or sets the snake_case account status.</summary>
    public string Status { get; set; } = null!;

    /// <summary>Gets or sets the end of a temporary suspension when applicable.</summary>
    public DateTime? SuspendedUntil { get; set; }

    /// <summary>Gets or sets the latest successful login timestamp.</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Gets or sets the account creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }
}
