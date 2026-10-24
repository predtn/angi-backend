using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Defines the RM-13 operation for liking or disliking the dish of one owned roadmap item.</summary>
public interface IRateRoadmapItemUseCase
{
    /// <summary>
    /// Records the feedback once per item; raises 404 ROADMAP_ITEM_NOT_FOUND outside an owned visible roadmap and
    /// 409 FEEDBACK_ALREADY_EXISTS when the item was already rated.
    /// </summary>
    Task<RateRoadmapItemResponseDto> ExecuteAsync(
        long roadmapId,
        long itemId,
        RateRoadmapItemRequestDto request,
        CancellationToken ct);
}
