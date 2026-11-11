namespace ANGI.Application.DTOs.Moderation;

/// <summary>Represents MOD-V1 filters and pagination for the verification review queue.</summary>
public sealed class ListVerificationsRequestDto
{
    public string Status { get; set; } = "pending";
    public string? Province { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
