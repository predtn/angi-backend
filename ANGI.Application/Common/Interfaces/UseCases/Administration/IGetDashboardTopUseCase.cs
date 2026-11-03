using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Defines the use case for retrieving top ranking entities (ADM-D3).</summary>
public interface IGetDashboardTopUseCase
{
    /// <summary>Validates parameters and returns top ranking entities.</summary>
    Task<DashboardTopDto> ExecuteAsync(GetDashboardTopRequestDto request, CancellationToken ct);
}
