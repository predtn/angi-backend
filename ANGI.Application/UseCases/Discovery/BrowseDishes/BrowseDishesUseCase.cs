using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Discovery;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Restaurant;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Discovery.BrowseDishes;

/// <summary>
/// Implements DISH-01: filter the candidates (at most 2000, by restaurant rating then like_count), let reco rank at most
/// 200 of them once, cache that list for 30 minutes, and page through it with a cursor.
/// </summary>
public sealed class BrowseDishesUseCase : IBrowseDishesUseCase
{
    public const int MaxCandidates = 2000;
    public const int MaxRanked = 200;
    public const int DefaultLimit = 20;
    public const decimal DefaultRadiusKm = 5;

    private const string InvalidCursorCode = "INVALID_CURSOR";
    private const string InvalidCursorMessage = "Danh sách đã hết hạn, vui lòng tải lại.";

    private static readonly IReadOnlyDictionary<string, DishForm> DishForms =
        Enum.GetValues<DishForm>().ToDictionary(form => JsonNamingPolicy.SnakeCaseLower.ConvertName(form.ToString()));

    private readonly IValidator<BrowseDishesRequestDto> _validator;
    private readonly IDishRepository _dishRepository;
    private readonly IRecommendationService _recommendationService;
    private readonly IRankedDishListCache _cache;
    private readonly ICurrentUserService _currentUserService;

    public BrowseDishesUseCase(
        IValidator<BrowseDishesRequestDto> validator,
        IDishRepository dishRepository,
        IRecommendationService recommendationService,
        IRankedDishListCache cache,
        ICurrentUserService currentUserService)
    {
        _validator = validator;
        _dishRepository = dishRepository;
        _recommendationService = recommendationService;
        _cache = cache;
        _currentUserService = currentUserService;
    }

    public async Task<BrowseDishesResponseDto> ExecuteAsync(BrowseDishesRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        // Only Travelers are personalized; any other caller is ranked like a Guest.
        var userId = _currentUserService.Role == "TRAVELER" ? _currentUserService.UserId : null;
        var limit = request.Limit ?? DefaultLimit;

        var (list, offset) = request.Cursor is null
            ? (await RankAsync(request, userId, ct), 0)
            : ResumeFromCursor(request.Cursor, userId);

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
        return new BrowseDishesResponseDto
        {
            RequestId = list.RequestId,
            Items = items,
            NextCursor = nextOffset < list.DishIds.Count ? DishListCursor.Encode(list.Key, nextOffset) : null
        };
    }

    private async Task<RankedDishList> RankAsync(BrowseDishesRequestDto request, int? userId, CancellationToken ct)
    {
        var filter = new DishCandidateFilter
        {
            Query = request.Q,
            Province = request.Province,
            TagIds = request.TagIds ?? [],
            ExcludeTagIds = request.ExcludeTagIds ?? [],
            DishForm = request.DishForm is null ? null : DishForms[request.DishForm],
            MinPrice = request.MinPrice,
            MaxPrice = request.MaxPrice,
            RestaurantId = request.RestaurantId,
            Area = request.Lat is { } lat && request.Lng is { } lng
                ? new NearbyArea(lat, lng, request.RadiusKm ?? DefaultRadiusKm)
                : null
        };

        var candidates = await _dishRepository.GetCandidateIdsAsync(filter, MaxCandidates, ct);
        var ranked = await _recommendationService.RecommendAsync(
            userId,
            candidates,
            MaxRanked,
            RecommendationContext.List,
            ct);

        var list = new RankedDishList(ranked.RequestId ?? Guid.NewGuid(), userId, ranked.RequestId, ranked.DishIds);
        _cache.Store(list);
        return list;
    }

    private (RankedDishList List, int Offset) ResumeFromCursor(string cursor, int? userId)
    {
        if (!DishListCursor.TryDecode(cursor, out var key, out var offset) ||
            _cache.Get(key) is not { } list ||
            list.UserId != userId ||
            offset > list.DishIds.Count)
        {
            throw new BadRequestException(InvalidCursorCode, InvalidCursorMessage);
        }

        return (list, offset);
    }
}
