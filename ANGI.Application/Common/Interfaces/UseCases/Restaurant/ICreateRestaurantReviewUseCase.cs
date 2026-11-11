using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Defines REV-01 use case contract: traveler submits a review for a restaurant.</summary>
public interface ICreateRestaurantReviewUseCase
{
    /// <summary>Submits a restaurant review and updates the restaurant's rating metrics.</summary>
    Task<ReviewDto> ExecuteAsync(
        long restaurantId,
        CreateRestaurantReviewRequestDto request,
        CancellationToken ct);
}
