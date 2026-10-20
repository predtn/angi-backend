namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Contains the trip overview accepted by RM-01.</summary>
public sealed class CreateRoadmapRequestDto
{
    public string Title { get; set; } = null!;
    public DateOnly? StartDate { get; set; }
    public short NumDays { get; set; }
    public decimal? BudgetAmount { get; set; }
    public string? OriginMode { get; set; }
    public decimal? OriginLatitude { get; set; }
    public decimal? OriginLongitude { get; set; }
    public string? OriginLabel { get; set; }
}
