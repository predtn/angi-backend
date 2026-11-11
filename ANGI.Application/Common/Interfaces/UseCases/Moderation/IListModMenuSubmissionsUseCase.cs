using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.Common.Models;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the ListModMenuSubmissions moderation operation.</summary>
public interface IListModMenuSubmissionsUseCase
{
    /// <summary>Executes the operation with caller cancellation.</summary>
    Task<Paged<ModMenuSubmissionCardDto>> ExecuteAsync(ListModMenuSubmissionsRequestDto request, CancellationToken ct);
}
