namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains the MOD-RP1 target, state, reason and paging filters.</summary>
public sealed class ListReportsRequestDto
{
    public string TargetType { get; init; } = string.Empty;
    public string? Status { get; init; }
    public string? ReasonCode { get; init; }
    public long? TargetId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
