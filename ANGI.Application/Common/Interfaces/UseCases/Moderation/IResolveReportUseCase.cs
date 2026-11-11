using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the ResolveReport moderation operation.</summary>
public interface IResolveReportUseCase
{
    /// <summary>Executes the operation with authorization and propagates request cancellation.</summary>
    Task<ReportDto> ExecuteAsync(long reportId, ResolveReportRequestDto request, CancellationToken ct);
}
