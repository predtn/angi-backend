using System.Text.Json.Serialization;

namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Encapsulates query parameters for OWN-19 owner restaurant reviews retrieval.</summary>
public sealed class GetOwnerReviewsRequestDto
{
    public bool? Replied { get; init; }
    public short? Rating { get; init; }
    public int Page { get; init; } = 1;

    [JsonPropertyName("pageIndex")]
    public int? PageIndex
    {
        get => Page;
        init => Page = value ?? Page;
    }

    public int PageSize { get; init; } = 20;
}
