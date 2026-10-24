namespace ANGI.Application.DTOs.Administration;

/// <summary>Response payload containing one MOD account's permission calculation.</summary>
public sealed class ModPermissionsDto
{
    /// <summary>Gets or sets permissions inherited from the MOD role.</summary>
    public IReadOnlyList<string> RoleDefaults { get; set; } = [];

    /// <summary>Gets or sets explicit allow and deny entries for the MOD account.</summary>
    public IReadOnlyList<PermissionOverrideDto> Overrides { get; set; } = [];

    /// <summary>Gets or sets role defaults plus allows minus denies.</summary>
    public IReadOnlyList<string> Effective { get; set; } = [];
}

/// <summary>Represents one permission override in requests and responses.</summary>
public sealed class PermissionOverrideDto
{
    /// <summary>Gets or sets the permission catalog code.</summary>
    public string PermissionCode { get; set; } = null!;

    /// <summary>Gets or sets either allow or deny.</summary>
    public string Effect { get; set; } = null!;
}
