using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the target-authorized MOD-RP1 report queue.</summary>
public interface IListReportsUseCase
{
    /// <summary>Validates filters and returns a page with evidence and open counts for each target.</summary>
    Task<Paged<ReportDto>> ExecuteAsync(ListReportsRequestDto request, CancellationToken ct);
}
