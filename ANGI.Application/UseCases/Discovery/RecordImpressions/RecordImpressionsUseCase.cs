using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;
using FluentValidation;

namespace ANGI.Application.UseCases.Discovery.RecordImpressions;

/// <summary>Implements IMP-01: nothing is stored in core; the impressions go straight to RECO-06.</summary>
public sealed class RecordImpressionsUseCase : IRecordImpressionsUseCase
{
    private readonly IValidator<RecordImpressionsRequestDto> _validator;
    private readonly ICurrentUserService _currentUserService;
    private readonly IRecommendationService _recommendationService;
    private readonly TimeProvider _timeProvider;

    public RecordImpressionsUseCase(
        IValidator<RecordImpressionsRequestDto> validator,
        ICurrentUserService currentUserService,
        IRecommendationService recommendationService,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _currentUserService = currentUserService;
        _recommendationService = recommendationService;
        _timeProvider = timeProvider;
    }

    public async Task<RecordImpressionsResponseDto> ExecuteAsync(RecordImpressionsRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Yêu cầu đăng nhập.");

        // RecordImpressionsAsync never throws for a reco failure: it logs it, and the app still gets 202.
        await _recommendationService.RecordImpressionsAsync(
            userId,
            request.RequestId!.Value,
            request.DishIds!,
            request.ShownAt ?? _timeProvider.GetUtcNow().UtcDateTime,
            ct);

        return new RecordImpressionsResponseDto { Accepted = true };
    }
}
