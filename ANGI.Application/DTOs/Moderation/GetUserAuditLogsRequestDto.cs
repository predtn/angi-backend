namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains MOD-U5 filter and pagination parameters for querying a user's audit logs.</summary>
public sealed class GetUserAuditLogsRequestDto
{
    private string? _action;
    private DateTime? _from;
    private DateTime? _to;

    /// <summary>Gets or sets the optional audit action code filter (e.g. USER_SUSPENDED, USER_LOGIN).</summary>
    public string? Action
    {
        get => _action;
        set => _action = value;
    }

    /// <summary>Gets or sets an alias for <see cref="Action"/>.</summary>
    public string? ActionType
    {
        get => _action;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _action = value;
            }
        }
    }

    /// <summary>Gets or sets the optional lower bound (inclusive) for the audit log timestamp.</summary>
    public DateTime? From
    {
        get => _from;
        set => _from = value;
    }

    /// <summary>Gets or sets an alias for <see cref="From"/>.</summary>
    public DateTime? StartDate
    {
        get => _from;
        set => _from ??= value;
    }

    /// <summary>Gets or sets the optional upper bound (inclusive) for the audit log timestamp.</summary>
    public DateTime? To
    {
        get => _to;
        set => _to = value;
    }

    /// <summary>Gets or sets an alias for <see cref="To"/>.</summary>
    public DateTime? EndDate
    {
        get => _to;
        set => _to ??= value;
    }

    /// <summary>Gets or sets the 1-based page number.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Gets or sets an alias for <see cref="Page"/>.</summary>
    public int? PageIndex
    {
        get => Page;
        set
        {
            if (value.HasValue)
            {
                Page = value.Value;
            }
        }
    }

    /// <summary>Gets or sets the number of items per page (1..100, default 20).</summary>
    public int PageSize { get; set; } = 20;
}
