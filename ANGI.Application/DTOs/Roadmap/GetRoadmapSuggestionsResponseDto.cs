using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Response data of RM-06.</summary>
public sealed class GetRoadmapSuggestionsResponseDto
{
    /// <summary>Reco's request_id; null when reco falls back without ranking.</summary>
    public Guid? RequestId { get; init; }

    public IReadOnlyList<DishCardDto> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}
