using ANGI.Application.DTOs.Social;

namespace ANGI.Application.Common.Interfaces.UseCases.Social;

/// <summary>Defines REP-02 to report a blog for moderation.</summary>
public interface IReportBlogUseCase
{
    /// <summary>Submits a violation report for the specified blog.</summary>
    Task<CreateReportResponseDto> ExecuteAsync(long blogId, ReportBlogRequestDto request, CancellationToken ct);
}
