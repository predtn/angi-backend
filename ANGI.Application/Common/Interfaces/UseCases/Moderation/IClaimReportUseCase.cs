using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the MOD-RP3 exclusive report claim operation.</summary>
public interface IClaimReportUseCase
{
    /// <summary>Atomically assigns a pending report to the authorized caller or rejects its current state.</summary>
    Task<ReportDto> ExecuteAsync(long reportId, CancellationToken ct);
}
