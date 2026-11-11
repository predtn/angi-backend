using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Describes a moderation queue entry and its proposed changes.</summary>
public sealed class ModMenuSubmissionCardDto : MenuSubmissionDto
{
    public RestaurantMiniDto Restaurant { get; set; } = new();
    public int NewCount { get; set; }
    public int EditedCount { get; set; }
    public int RemovedCount { get; set; }
}
