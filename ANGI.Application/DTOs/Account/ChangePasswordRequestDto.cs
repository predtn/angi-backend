namespace ANGI.Application.DTOs.Account;

/// <summary>Request body for changing the authenticated user's password (ME-03).</summary>
public sealed class ChangePasswordRequestDto
{
    /// <summary>Current password of the account. Optional if the user previously only logged in via Google.</summary>
    public string? CurrentPassword { get; set; }

    /// <summary>Alias for CurrentPassword if client sends oldPassword.</summary>
    public string? OldPassword
    {
        get => CurrentPassword;
        set => CurrentPassword ??= value;
    }

    /// <summary>New password satisfying length (8..64), containing letters and digits, differing from current password.</summary>
    public string NewPassword { get; set; } = null!;
}
