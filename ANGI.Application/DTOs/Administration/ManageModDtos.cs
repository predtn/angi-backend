namespace ANGI.Application.DTOs.Administration;

/// <summary>Represents ADM-M1 filters and pagination.</summary>
public sealed class ListModsRequestDto
{
    /// <summary>Gets or sets an optional email or display-name search term.</summary>
    public string? Q { get; set; }

    /// <summary>Gets or sets an optional active or deactivated status filter.</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the one-based page number.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Gets or sets the requested page size.</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>Represents the ADM-M2 MOD invitation payload.</summary>
public sealed class CreateModRequestDto
{
    /// <summary>Gets or sets the unique MOD email address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the MOD display name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets optional permission overrides applied during creation.</summary>
    public List<PermissionOverrideDto>? PermissionOverrides { get; set; }
}

/// <summary>Represents the ADM-M4 partial MOD account update.</summary>
public sealed class UpdateModRequestDto
{
    /// <summary>Gets or sets a replacement display name when supplied.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets active or deactivated when supplied.</summary>
    public string? Status { get; set; }
}

/// <summary>Represents the ADM-M3 full MOD account response.</summary>
public sealed class ModDetailDto : AdminUserCardDto
{
    /// <summary>Gets or sets the MOD default, override, and effective permissions.</summary>
    public ModPermissionsDto Permissions { get; set; } = new();

    /// <summary>Gets or sets all-time moderation handling counts.</summary>
    public ModHandledDto Handled { get; set; } = new();
}

/// <summary>Contains all-time work handled by one MOD account.</summary>
public sealed class ModHandledDto
{
    /// <summary>Gets or sets the number of reviewed restaurant verifications.</summary>
    public int Verifications { get; set; }

    /// <summary>Gets or sets the number of reviewed menu submissions.</summary>
    public int Menus { get; set; }

    /// <summary>Gets or sets the number of handled reports.</summary>
    public int Reports { get; set; }
}
