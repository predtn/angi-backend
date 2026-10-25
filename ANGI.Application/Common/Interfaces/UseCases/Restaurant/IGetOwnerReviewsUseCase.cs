using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Defines the contract for OWN-19 retrieving the authenticated owner's restaurant reviews.</summary>
public interface IGetOwnerReviewsUseCase
{
    Task<Paged<ReviewDto>> ExecuteAsync(GetOwnerReviewsRequestDto request, CancellationToken ct);
}
