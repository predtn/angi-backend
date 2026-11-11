namespace ANGI.Application.DTOs.Moderation;

/// <summary>Extends Report with the RestaurantCard or BlogDetail target and other open reports.</summary>
public sealed class ReportDetailDto : ReportDto
{
    public object Target { get; init; } = null!;
    public IReadOnlyList<ReportDto> RelatedReports { get; init; } = [];
}
