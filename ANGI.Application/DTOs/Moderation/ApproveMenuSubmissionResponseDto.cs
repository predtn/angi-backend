using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains the approved submission and the identifiers affected by MOD-M3.</summary>
public sealed class ApproveMenuSubmissionResponseDto : MenuSubmissionDto
{
    public IReadOnlyList<int> CreatedDishIds { get; set; } = [];
    public IReadOnlyList<int> UpdatedDishIds { get; set; } = [];
    public IReadOnlyList<int> RemovedDishIds { get; set; } = [];
}
