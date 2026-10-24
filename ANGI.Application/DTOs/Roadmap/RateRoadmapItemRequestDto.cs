namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Request body of RM-13.</summary>
public sealed class RateRoadmapItemRequestDto
{
    /// <summary>like | dislike; each roadmap item is rated once.</summary>
    public string Value { get; init; } = null!;
}
