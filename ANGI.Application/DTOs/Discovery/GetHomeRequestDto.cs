namespace ANGI.Application.DTOs.Discovery;

/// <summary>Query string of HOME-01.</summary>
public sealed class GetHomeRequestDto
{
    /// <summary>Case-insensitive substring of restaurants.province_name; blogs are not filtered (they have no province).</summary>
    public string? Province { get; init; }

    /// <summary>Items per block, 1..50, 10 by default.</summary>
    public int? Limit { get; init; }
}
