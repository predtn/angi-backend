using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Defines the use case for retrieving overall dashboard summary statistics (ADM-D1).</summary>
public interface IGetDashboardSummaryUseCase
{
    /// <summary>Validates the date range and retrieves aggregated dashboard summary metrics.</summary>
    Task<DashboardSummaryDto> ExecuteAsync(GetDashboardSummaryRequestDto request, CancellationToken ct);
}
