namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Encapsulates pagination query parameters for RM-02.</summary>
public sealed class GetMyRoadmapsRequestDto
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
