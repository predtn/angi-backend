using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the MOD-RP2 report detail operation.</summary>
public interface IGetReportUseCase
{
    /// <summary>Authorizes the report target and returns its content and other open reports.</summary>
    Task<ReportDetailDto> ExecuteAsync(long reportId, CancellationToken ct);
}
