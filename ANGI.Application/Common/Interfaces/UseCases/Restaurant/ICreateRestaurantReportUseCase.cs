using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Executes the REP-01 use case to report a restaurant.</summary>
public interface ICreateRestaurantReportUseCase
{
    /// <summary>Creates a pending moderation report for a restaurant.</summary>
    Task<CreateRestaurantReportResponseDto> ExecuteAsync(
        long restaurantId,
        CreateRestaurantReportRequestDto request,
        CancellationToken ct);
}
