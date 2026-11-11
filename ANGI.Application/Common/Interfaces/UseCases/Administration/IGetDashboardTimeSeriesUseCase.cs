using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Defines the use case for retrieving time-series metrics for charts (ADM-D2).</summary>
public interface IGetDashboardTimeSeriesUseCase
{
    /// <summary>Validates request boundaries and returns time-series chart data.</summary>
    Task<DashboardTimeSeriesDto> ExecuteAsync(GetDashboardTimeSeriesRequestDto request, CancellationToken ct);
}
