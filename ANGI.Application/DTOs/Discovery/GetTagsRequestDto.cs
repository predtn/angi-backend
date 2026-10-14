namespace ANGI.Application.DTOs.Discovery;

/// <summary>Query string of CAT-02.</summary>
public sealed class GetTagsRequestDto
{
    /// <summary>meal_time | dietary | category; every tag when omitted.</summary>
    public string? Type { get; init; }
}
