using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Defines ADM-M1 MOD account listing.</summary>
public interface IListModsUseCase
{
    /// <summary>Validates filters and returns one page of MOD accounts.</summary>
    Task<Paged<AdminUserCardDto>> ExecuteAsync(ListModsRequestDto request, CancellationToken ct);
}
