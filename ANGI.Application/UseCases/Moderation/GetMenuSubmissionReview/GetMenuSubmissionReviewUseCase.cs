using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.UseCases.Moderation.GetMenuSubmissionReview;

/// <summary>Implements MOD-M2 menu comparison for an authorized reviewer.</summary>
public sealed class GetMenuSubmissionReviewUseCase(IMenuReviewRepository repository, ICurrentUserService currentUser)
    : IGetMenuSubmissionReviewUseCase
{
    /// <summary>Loads a submission and maps proposed, previous and removed dishes.</summary>
    public async Task<ModMenuSubmissionDetailDto> ExecuteAsync(long submissionId, CancellationToken ct)
    {
        await MenuPermissionGuard.RequireAsync(currentUser, repository, ct);
        var submission = await repository.GetDetailAsync(submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_NOT_FOUND", "Không tìm thấy bản menu.");
        return MenuReviewMapper.Detail(submission);
    }
}
