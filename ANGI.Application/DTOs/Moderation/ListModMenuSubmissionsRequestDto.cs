namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains MOD-M1 queue filters and pagination.</summary>
public sealed class ListModMenuSubmissionsRequestDto
{
    public string Status { get; set; } = "pending";
    public long? RestaurantId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
