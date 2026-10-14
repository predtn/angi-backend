namespace ANGI.Application.DTOs.Discovery;

/// <summary>Response data of DISH-01.</summary>
public sealed class BrowseDishesResponseDto
{
    /// <summary>Reco's request_id, sent back in IMP-01; null for a Guest or when reco could not rank.</summary>
    public Guid? RequestId { get; init; }

    public IReadOnlyList<DishCardDto> Items { get; init; } = [];

    /// <summary>Sent back to get the next page; null on the last page.</summary>
    public string? NextCursor { get; init; }
}
