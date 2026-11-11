namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Contains OWN-12 status and page filters.</summary>
public sealed class ListMenuSubmissionsRequestDto
{
    public string? Status { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
