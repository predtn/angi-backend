using System.Text.Json.Serialization;

namespace ANGI.Application.DTOs.Administration;

/// <summary>Response payload containing all permissions and role defaults (ADM-P1).</summary>
public sealed class PermissionCatalogDto
{
    /// <summary>Gets or sets the complete permission catalog.</summary>
    public IReadOnlyList<PermissionDto> Permissions { get; set; } = [];

    /// <summary>Gets or sets default permissions keyed by administrative role.</summary>
    public RolePermissionDefaultsDto RoleDefaults { get; set; } = new();
}

/// <summary>Describes one permission that can be displayed by the administration client.</summary>
public sealed class PermissionDto
{
    /// <summary>Gets or sets the stable permission code.</summary>
    public string Code { get; set; } = null!;

    /// <summary>Gets or sets the user-facing permission description.</summary>
    public string Description { get; set; } = null!;
}

/// <summary>Contains the documented uppercase role keys in the ADM-P1 response.</summary>
public sealed class RolePermissionDefaultsDto
{
    /// <summary>Gets or sets the current MOD role defaults.</summary>
    [JsonPropertyName("MOD")]
    public IReadOnlyList<string> Mod { get; set; } = [];

    /// <summary>Gets or sets the ADMIN wildcard because administrators always have every permission.</summary>
    [JsonPropertyName("ADMIN")]
    public IReadOnlyList<string> Admin { get; set; } = ["*"];
}
