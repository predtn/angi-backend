namespace ANGI.Application.DTOs.Administration;

/// <summary>Request payload that replaces every default permission of the MOD role (ADM-P4).</summary>
public sealed class ReplaceModRolePermissionsRequestDto
{
    /// <summary>Gets or sets the complete replacement permission-code list.</summary>
    public List<string> PermissionCodes { get; set; } = null!;
}

/// <summary>Response payload containing the MOD role defaults after replacement.</summary>
public sealed class ReplaceModRolePermissionsResponseDto
{
    /// <summary>Gets or sets the role code, always MOD.</summary>
    public string Role { get; set; } = "MOD";

    /// <summary>Gets or sets the permission codes stored for the MOD role.</summary>
    public IReadOnlyList<string> PermissionCodes { get; set; } = [];
}
