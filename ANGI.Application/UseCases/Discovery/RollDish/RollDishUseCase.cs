using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Discovery;
using ANGI.Domain.Entities;
using FluentValidation;

namespace ANGI.Application.UseCases.Discovery.RollDish;

/// <summary>
/// Implements DISH-03: filter the candidates, let reco rank its top 10 (context = roll), pick one at random weighted by
/// recommend_score (evenly when reco gave no usable score), store the roll and report the shown dish to reco.
/// </summary>
public sealed class RollDishUseCase : IRollDishUseCase
{
    public const int MaxCandidates = 2000;
    public const int TopK = 10;
    public const decimal DefaultRadiusKm = 3;

    private const string NoCandidateCode = "NO_CANDIDATE";
    private const string NoCandidateMessage = "Không còn món phù hợp. Hãy thử nới bán kính hoặc bỏ bớt bộ lọc.";

    private readonly IValidator<RollDishRequestDto> _validator;
    private readonly IDishRepository _dishRepository;
    private readonly IRecommendationService _recommendationService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;

    public RollDishUseCase(
        IValidator<RollDishRequestDto> validator,
        IDishRepository dishRepository,
        IRecommendationService recommendationService,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        Random random)
    {
        _validator = validator;
        _dishRepository = dishRepository;
        _recommendationService = recommendationService;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _random = random;
    }

    public async Task<RollDishResponseDto> ExecuteAsync(RollDishRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Yêu cầu đăng nhập.");
        var area = request.Latitude is { } latitude && request.Longitude is { } longitude
            ? new NearbyArea(latitude, longitude, request.RadiusKm ?? DefaultRadiusKm)
            : null;

        var candidates = await _dishRepository.GetCandidateIdsAsync(new DishCandidateFilter
        {
            Province = request.Province,
            AnyTagIds = request.TagIds ?? [],
            ExcludeTagIds = request.ExcludeTagIds ?? [],
            MealTimeCode = request.MealTime,
            MaxPrice = request.MaxPrice,
            ExcludeDishIds = request.ExcludeDishIds ?? [],
            Area = area
        }, MaxCandidates, ct);
        if (candidates.Count == 0)
        {
            throw new NotFoundException(NoCandidateCode, NoCandidateMessage);
        }

        var ranked = await _recommendationService.RecommendAsync(userId, candidates, TopK, RecommendationContext.Roll, ct);
        if (ranked.DishIds.Count == 0)
        {
            throw new NotFoundException(NoCandidateCode, NoCandidateMessage);
        }

        var index = Pick(ranked.Scores, ranked.DishIds.Count);
        var dish = await _dishRepository.GetPublicDetailAsync(ranked.DishIds[index], ct)
            ?? throw new NotFoundException(NoCandidateCode, NoCandidateMessage);

        var roll = new DishRoll { UserId = userId, DishId = dish.Id, RecoRequestId = ranked.RequestId };
        _dishRepository.AddRoll(roll);
        await _unitOfWork.SaveChangesAsync(ct);

        if (ranked.RequestId is { } requestId)
        {
            // The app does not send IMP-01 for a roll: the backend reports the shown dish itself. Never throws.
            await _recommendationService.RecordImpressionsAsync(
                userId, requestId, [dish.Id], _timeProvider.GetUtcNow().UtcDateTime, ct);
        }

        decimal? distanceKm = area is null
            ? null
            : Math.Round((decimal)NearbyArea.DistanceKm(area.Latitude, area.Longitude, dish.Restaurant.Latitude, dish.Restaurant.Longitude), 1);
        return new RollDishResponseDto
        {
            RollId = roll.Id,
            RequestId = ranked.RequestId,
            Dish = DishDetailMapper.Map(dish, _timeProvider, rank: index + 1, distanceKm)
        };
    }

    /// <summary>Index drawn with probability proportional to its score; uniform without positive scores.</summary>
    private int Pick(IReadOnlyList<double>? scores, int count)
    {
        var weights = scores is { } values && values.Count == count
            ? values.Select(score => double.IsFinite(score) && score > 0 ? score : 0).ToArray()
            : null;
        var total = weights?.Sum() ?? 0;
        if (weights is null || total <= 0)
        {
            return _random.Next(count);
        }

        var target = _random.NextDouble() * total;
        for (var index = 0; index < count; index++)
        {
            target -= weights[index];
            if (target < 0)
            {
                return index;
            }
        }

        return count - 1;
    }
}
