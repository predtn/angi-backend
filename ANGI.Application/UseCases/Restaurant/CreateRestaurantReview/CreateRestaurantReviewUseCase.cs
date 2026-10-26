using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.CreateRestaurantReview;

/// <summary>Implements REV-01: traveler posts a review for a restaurant and updates rating statistics.</summary>
public sealed class CreateRestaurantReviewUseCase : ICreateRestaurantReviewUseCase
{
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<CreateRestaurantReviewRequestDto> _validator;

    public CreateRestaurantReviewUseCase(
        IRestaurantRepository restaurantRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork,
        IValidator<CreateRestaurantReviewRequestDto> validator)
    {
        _restaurantRepository = restaurantRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
        _validator = validator;
    }

    /// <summary>Submits a restaurant review and recalculates average rating and review count.</summary>
    public async Task<ReviewDto> ExecuteAsync(
        long restaurantId,
        CreateRestaurantReviewRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để đánh giá.");

        var user = await _restaurantRepository.GetReviewerUserAsync(userId, ct)
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Tài khoản không còn tồn tại.");

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

        var restaurant = await _restaurantRepository.LockRestaurantForReviewAsync(restaurantId, ct);
        if (restaurant == null)
        {
            throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
        }

        var hasReviewed = await _restaurantRepository.HasUserReviewedRestaurantAsync(restaurantId, userId, ct);
        if (hasReviewed)
        {
            throw new ConflictException("REVIEW_ALREADY_EXISTS", "Bạn đã đánh giá nhà hàng này.");
        }

        if (request.RoadmapItemId.HasValue)
        {
            var isRoadmapValid = await _restaurantRepository.IsRoadmapItemValidForRestaurantAsync(
                userId,
                request.RoadmapItemId.Value,
                restaurantId,
                ct);

            if (!isRoadmapValid)
            {
                throw new BadRequestException("VALIDATION_FAILED", "Mục lịch trình không hợp lệ hoặc không thuộc nhà hàng này.");
            }
        }

        IReadOnlyList<MediaFile> mediaFiles = Array.Empty<MediaFile>();
        if (request.ImageMediaIds is { Count: > 0 })
        {
            var distinctIds = request.ImageMediaIds.Distinct().ToArray();
            mediaFiles = await _restaurantRepository.GetUserReviewMediaFilesAsync(userId, distinctIds, ct);

            if (mediaFiles.Count != distinctIds.Length)
            {
                throw new BadRequestException("VALIDATION_FAILED", "Một số hình ảnh không hợp lệ hoặc không thuộc quyền sở hữu của bạn.");
            }
        }

        var review = new RestaurantReview
        {
            RestaurantId = restaurantId,
            UserId = userId,
            Rating = request.Rating,
            Content = string.IsNullOrWhiteSpace(request.Content) ? null : request.Content.Trim(),
            RoadmapItemId = request.RoadmapItemId,
            Status = ContentStatus.Visible,
            User = user,
            Restaurant = restaurant
        };

        foreach (var media in mediaFiles)
        {
            review.Images.Add(new ReviewImage
            {
                Review = review,
                MediaId = media.Id,
                Media = media
            });
        }

        _restaurantRepository.AddReview(review);

        // Recompute from the visible reviews (the restaurant row is locked): adding to the rounded rating_avg would
        // let the rounding error grow with every review.
        var (visibleCount, ratingSum) = await _restaurantRepository.GetVisibleRatingStatsAsync(restaurantId, ct);
        var newCount = visibleCount + 1;
        restaurant.RatingCount = newCount;
        restaurant.RatingAvg = Math.Round((decimal)(ratingSum + request.Rating) / newCount, 2, MidpointRounding.AwayFromZero);

        if (restaurant.OwnerId != userId)
        {
            _restaurantRepository.AddNotification(new Domain.Entities.Notification
            {
                RecipientId = restaurant.OwnerId,
                Type = "RESTAURANT_REVIEWED",
                Title = "Đánh giá mới cho nhà hàng",
                Body = $"{user.DisplayName} đã đánh giá {request.Rating} sao cho nhà hàng {restaurant.Name}.",
                Data = JsonSerializer.Serialize(new { restaurantId = restaurant.Id, reviewId = review.Id })
            });
        }

        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return RestaurantDetailMapper.MapReview(review);
    }
}
