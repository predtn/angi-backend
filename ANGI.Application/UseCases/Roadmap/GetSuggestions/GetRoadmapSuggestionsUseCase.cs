using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Discovery;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Discovery;
using ANGI.Application.UseCases.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Roadmap.GetSuggestions;

/// <summary>Implements RM-06 by filtering, ranking once, caching, and cursor-paging roadmap suggestions.</summary>
public sealed class GetRoadmapSuggestionsUseCase : IGetRoadmapSuggestionsUseCase
{
    public const int MaxCandidates = 2000;
    public const int MaxRanked = 200;
    public const int DefaultLimit = 20;
    public const decimal DefaultRadiusKm = 10;

    private const string InvalidCursorCode = "INVALID_CURSOR";
    private const string InvalidCursorMessage = "Danh sách đã hết hạn, vui lòng tải lại.";

    private readonly IValidator<GetRoadmapSuggestionsRequestDto> _validator;
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly IDishRepository _dishRepository;
    private readonly IRecommendationService _recommendationService;
    private readonly IRankedDishListCache _cache;
    private readonly ICurrentUserService _currentUserService;

    /// <summary>Initializes validation, owned-roadmap reads, ranking, caching, and identity dependencies.</summary>
    public GetRoadmapSuggestionsUseCase(
        IValidator<GetRoadmapSuggestionsRequestDto> validator,
        IRoadmapRepository roadmapRepository,
        IDishRepository dishRepository,
        IRecommendationService recommendationService,
        IRankedDishListCache cache,
        ICurrentUserService currentUserService)
    {
        _validator = validator;
        _roadmapRepository = roadmapRepository;
        _dishRepository = dishRepository;
        _recommendationService = recommendationService;
        _cache = cache;
        _currentUserService = currentUserService;
    }

    /// <summary>Returns an ownership-scoped RM-06 page while preserving reco ranks across cursor requests.</summary>
    public async Task<GetRoadmapSuggestionsResponseDto> ExecuteAsync(
        long roadmapId,
        GetRoadmapSuggestionsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem gợi ý roadmap.");
        var context = await _roadmapRepository.GetSuggestionContextAsync(roadmapId, userId, ct)
            ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
        var limit = request.Limit ?? DefaultLimit;
        var (list, offset) = request.Cursor is null
            ? (await RankAsync(roadmapId, userId, request, context, ct), 0)
            : ResumeFromCursor(request.Cursor, roadmapId, userId);

        var pageIds = list.DishIds.Skip(offset).Take(limit).ToArray();
        var dishes = (await _dishRepository.GetCardDishesAsync(pageIds, ct)).ToDictionary(dish => dish.Id);
        var items = pageIds
            .Select((dishId, index) => (dishId, rank: offset + index + 1))
            .Where(entry => dishes.ContainsKey(entry.dishId))
            .Select(entry =>
            {
                var dish = dishes[entry.dishId];
                var card = RestaurantDetailMapper.MapDishCard(dish, dish.Restaurant);
                card.Rank = entry.rank;
                return card;
            })
            .ToArray();

        var nextOffset = offset + limit;
        return new GetRoadmapSuggestionsResponseDto
        {
            RequestId = list.RequestId,
            Items = items,
            NextCursor = nextOffset < list.DishIds.Count ? DishListCursor.Encode(list.Key, nextOffset) : null
        };
    }

    /// <summary>Builds RM-06 candidate filters, calls RECO-05 once, and stores the stable ranked list.</summary>
    private async Task<RankedDishList> RankAsync(
        long roadmapId,
        int userId,
        GetRoadmapSuggestionsRequestDto request,
        RoadmapSuggestionContext context,
        CancellationToken ct)
    {
        var area = ResolveArea(request, context);
        var candidates = await _dishRepository.GetCandidateIdsAsync(new DishCandidateFilter
        {
            Query = request.Q,
            Province = request.Province,
            TagIds = request.TagIds ?? [],
            ExcludeTagIds = request.ExcludeTagIds ?? [],
            MealTimeCode = request.MealSlot,
            MaxPrice = request.MaxPrice,
            ExcludeDishIds = context.ExistingDishIds,
            Area = area
        }, MaxCandidates, ct);
        var ranked = await _recommendationService.RecommendAsync(
            userId,
            candidates,
            MaxRanked,
            RecommendationContext.RoadmapManual,
            ct);
        var list = new RankedDishList(
            ranked.RequestId ?? Guid.NewGuid(),
            userId,
            ranked.RequestId,
            ranked.DishIds,
            roadmapId);
        _cache.Store(list);
        return list;
    }

    /// <summary>Uses request GPS first, then the roadmap's pinned point, or no radius when neither exists.</summary>
    private static NearbyArea? ResolveArea(
        GetRoadmapSuggestionsRequestDto request,
        RoadmapSuggestionContext context)
    {
        if (request.Lat is { } latitude && request.Lng is { } longitude)
        {
            return new NearbyArea(latitude, longitude, request.RadiusKm ?? DefaultRadiusKm);
        }

        return context.OriginMode == ANGI.Domain.Enums.RoadmapOriginMode.Pinned &&
               context.OriginLatitude is { } pinnedLatitude &&
               context.OriginLongitude is { } pinnedLongitude
            ? new NearbyArea(pinnedLatitude, pinnedLongitude, request.RadiusKm ?? DefaultRadiusKm)
            : null;
    }

    /// <summary>Decodes and ownership-checks a cached cursor, including its originating roadmap.</summary>
    private (RankedDishList List, int Offset) ResumeFromCursor(string cursor, long roadmapId, int userId)
    {
        if (!DishListCursor.TryDecode(cursor, out var key, out var offset) ||
            _cache.Get(key) is not { } list ||
            list.UserId != userId ||
            list.ScopeId != roadmapId ||
            offset > list.DishIds.Count)
        {
            throw new BadRequestException(InvalidCursorCode, InvalidCursorMessage);
        }

        return (list, offset);
    }
}
