namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains MOD-U1 filters and pagination parameters for user moderation.</summary>
public sealed class ListUsersRequestDto
{
    /// <summary>Gets or sets the search keyword for email or display name.</summary>
    public string? Q { get; set; }

    /// <summary>Gets or sets an alias for Q.</summary>
    public string? Keyword
    {
        get => Q;
        set { if (!string.IsNullOrWhiteSpace(value)) Q = value; }
    }

    /// <summary>Gets or sets the optional role filter (TRAVELER or RESTAURANT_OWNER).</summary>
    public string? Role { get; set; }

    /// <summary>Gets or sets the optional account status filter.</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets a value indicating whether to filter by users having pending/in-review reports against their content.</summary>
    public bool? HasPendingReports { get; set; }

    /// <summary>Gets or sets the 1-based page number.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Gets or sets an alias for Page.</summary>
    public int? PageIndex
    {
        get => Page;
        set { if (value.HasValue) Page = value.Value; }
    }

    /// <summary>Gets or sets the number of items per page.</summary>
    public int PageSize { get; set; } = 20;
}
