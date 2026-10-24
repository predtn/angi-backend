namespace ANGI.Application.DTOs.Administration;

/// <summary>Request payload that replaces every override of a MOD account (ADM-P3).</summary>
public sealed class ReplaceModPermissionsRequestDto
{
    /// <summary>Gets or sets the complete replacement override list; an empty list removes all overrides.</summary>
    public List<PermissionOverrideDto> Overrides { get; set; } = null!;
}
