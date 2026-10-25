namespace ANGI.Application.DTOs.Social;

/// <summary>Request body of REP-02.</summary>
public sealed class ReportBlogRequestDto
{
    private string _reasonCode = string.Empty;
    private string? _description;

    /// <summary>spam | offensive | wrong_info | scam | other.</summary>
    public string ReasonCode
    {
        get => _reasonCode;
        set => _reasonCode = value;
    }

    /// <summary>Alias for ReasonCode (lý do báo cáo).</summary>
    public string? Reason
    {
        get => _reasonCode;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _reasonCode = value;
            }
        }
    }

    /// <summary>Optional description (≤ 1000 characters; required when reasonCode = other).</summary>
    public string? Description
    {
        get => _description;
        set => _description = value;
    }

    /// <summary>Alias for Description (mô tả chi tiết).</summary>
    public string? Details
    {
        get => _description;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _description = value;
            }
        }
    }

    /// <summary>Optional list of media identifiers (≤ 5).</summary>
    public IReadOnlyList<long>? EvidenceMediaIds { get; set; }
}
