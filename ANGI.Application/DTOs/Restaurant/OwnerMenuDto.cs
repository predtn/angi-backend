using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents the active owner menu and its optional open submission.</summary>
public sealed class OwnerMenuDto
{
    public IReadOnlyList<OwnerMenuDishDto> Dishes { get; set; } = Array.Empty<OwnerMenuDishDto>();
    public OpenMenuSubmissionDto? OpenSubmission { get; set; }
}

/// <summary>Adds owner-only status and ordering fields to a shared dish card.</summary>
public sealed class OwnerMenuDishDto : DishCardDto
{
    public string Status { get; set; } = string.Empty;
    public short SortOrder { get; set; }
}
