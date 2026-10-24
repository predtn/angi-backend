using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.UseCases.Moderation.GetVerificationReview;

/// <summary>Implements MOD-V2 complete verification detail retrieval.</summary>
public sealed class GetVerificationReviewUseCase : IGetVerificationReviewUseCase
{
    private readonly IVerificationReviewRepository _repository;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICloudinaryService _cloudinaryService;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes authorization, persistence, signed-media, and clock dependencies.</summary>
    public GetVerificationReviewUseCase(
        IVerificationReviewRepository repository,
        ICurrentUserService currentUserService,
        ICloudinaryService cloudinaryService,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _currentUserService = currentUserService;
        _cloudinaryService = cloudinaryService;
        _timeProvider = timeProvider;
    }

    /// <summary>Enforces RESTAURANT_VERIFY and returns MOD-V2 or VERIFICATION_NOT_FOUND.</summary>
    public async Task<VerificationReviewDetailDto> ExecuteAsync(long verificationId, CancellationToken ct)
    {
        await ModerationPermissionGuard.RequireAsync(_currentUserService, _repository, ct);
        var verification = await _repository.GetDetailAsync(verificationId, ct)
            ?? throw new NotFoundException("VERIFICATION_NOT_FOUND", "Không tìm thấy hồ sơ xác minh.");

        return ModerationVerificationMapper.MapDetail(verification, _cloudinaryService, _timeProvider);
    }
}
