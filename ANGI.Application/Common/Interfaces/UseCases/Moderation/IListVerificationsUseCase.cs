using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines MOD-V1 verification queue retrieval.</summary>
public interface IListVerificationsUseCase
{
    /// <summary>Returns the filtered verification queue for an authorized staff member.</summary>
    Task<Paged<ModVerificationCardDto>> ExecuteAsync(ListVerificationsRequestDto request, CancellationToken ct);
}
