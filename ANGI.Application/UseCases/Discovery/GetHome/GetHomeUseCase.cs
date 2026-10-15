using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Restaurant;
using ANGI.Domain.Entities;
using FluentValidation;

namespace ANGI.Application.UseCases.Discovery.GetHome;

/// <summary>
/// Implements HOME-01: trending dishes (likes in 7 days, completed by like_count), top restaurants (REST-01 by rating),
/// featured blogs (last 30 days first, by likes + comments) and dishes ranked by reco with context = home.
/// </summary>
public sealed class GetHomeUseCase : IGetHomeUseCase
{
    public const int DefaultLimit = 10;
    public const int MaxCandidates = 2000;
    public const int ExcerptLength = 200;
    public static readonly TimeSpan TrendingWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan FeaturedBlogWindow = TimeSpan.FromDays(30);

    private readonly IValidator<GetHomeRequestDto> _validator;
    private readonly IDishRepository _dishRepository;
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly IBlogRepository _blogRepository;
    private readonly IRecommendationService _recommendationService;
    private readonly ICurrentUserService _currentUserService;
    private readonly TimeProvider _timeProvider;

    public GetHomeUseCase(
        IValidator<GetHomeRequestDto> validator,
        IDishRepository dishRepository,
        IRestaurantRepository restaurantRepository,
        IBlogRepository blogRepository,
        IRecommendationService recommendationService,
        ICurrentUserService currentUserService,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _dishRepository = dishRepository;
        _restaurantRepository = restaurantRepository;
        _blogRepository = blogRepository;
        _recommendationService = recommendationService;
        _currentUserService = currentUserService;
        _timeProvider = timeProvider;
    }

    public async Task<GetHomeResponseDto> ExecuteAsync(GetHomeRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var limit = request.Limit ?? DefaultLimit;
        var now = _timeProvider.GetUtcNow();

        var trendingIds = await _dishRepository.GetTrendingIdsAsync(
            request.Province, now.UtcDateTime - TrendingWindow, limit, ct);
        var trending = await CardsAsync(trendingIds, ranked: false, ct);

        var topRestaurants = await _restaurantRepository.BrowseAsync(
            new BrowseRestaurantsRequestDto { Province = request.Province, Sort = "rating", Page = 1, PageSize = limit },
            now,
            ct);

        var blogs = await _blogRepository.GetFeaturedAsync(now.UtcDateTime - FeaturedBlogWindow, limit, ct);

        // Only Travelers are personalized; any other caller is ranked like a Guest (same rule as DISH-01).
        var userId = _currentUserService.Role == "TRAVELER" ? _currentUserService.UserId : null;
        var candidates = await _dishRepository.GetCandidateIdsAsync(
            new DishCandidateFilter { Province = request.Province }, MaxCandidates, ct);
        var ranked = await _recommendationService.RecommendAsync(userId, candidates, limit, RecommendationContext.Home, ct);

        return new GetHomeResponseDto
        {
            TrendingDishes = trending,
            TopRestaurants = topRestaurants.Items,
            FeaturedBlogs = blogs.Select(MapBlog).ToArray(),
            Recommended = new HomeRecommendedDto
            {
                RequestId = ranked.RequestId,
                Items = await CardsAsync(ranked.DishIds, ranked: true, ct)
            }
        };
    }

    /// <summary>DishCards in the order of <paramref name="dishIds"/>; rank is the position when the list is ranked.</summary>
    private async Task<IReadOnlyList<DishCardDto>> CardsAsync(IReadOnlyList<int> dishIds, bool ranked, CancellationToken ct)
    {
        var dishes = (await _dishRepository.GetCardDishesAsync(dishIds, ct)).ToDictionary(dish => dish.Id);
        return dishIds
            .Select((dishId, index) => (dishId, rank: index + 1))
            .Where(entry => dishes.ContainsKey(entry.dishId))
            .Select(entry =>
            {
                var dish = dishes[entry.dishId];
                var card = RestaurantDetailMapper.MapDishCard(dish, dish.Restaurant);
                card.Rank = ranked ? entry.rank : null;
                return card;
            })
            .ToArray();
    }

    private static BlogCardDto MapBlog(Blog blog) => new()
    {
        Id = blog.Id,
        Title = blog.Title,
        Slug = blog.Slug,
        Excerpt = blog.Content.Length <= ExcerptLength ? blog.Content.Trim() : blog.Content[..ExcerptLength].TrimEnd(),
        CoverUrl = blog.CoverMedia?.Url,
        Author = new UserSummaryDto
        {
            Id = blog.Author.Id,
            DisplayName = blog.Author.DisplayName,
            AvatarUrl = blog.Author.AvatarMedia?.Url
        },
        HasRoadmap = blog.RoadmapSnapshot is not null,
        LikeCount = blog.LikeCount,
        CommentCount = blog.CommentCount,
        ViewCount = blog.ViewCount,
        PublishedAt = blog.PublishedAt
    };
}
