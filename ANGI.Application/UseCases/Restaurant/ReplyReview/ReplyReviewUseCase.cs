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

namespace ANGI.Application.UseCases.Restaurant.ReplyReview;

/// <summary>Implements OWN-20 allowing a restaurant owner to create or update their reply to a review.</summary>
public sealed class ReplyReviewUseCase : IReplyReviewUseCase
{
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<ReplyReviewRequestDto> _validator;

    public ReplyReviewUseCase(
        IRestaurantRepository restaurantRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork,
        IValidator<ReplyReviewRequestDto> validator)
    {
        _restaurantRepository = restaurantRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
        _validator = validator;
    }

    /// <summary>Creates or modifies the authenticated owner's response to a customer review.</summary>
    public async Task<ReviewReplyDto> ExecuteAsync(
        long reviewId,
        ReplyReviewRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var ownerId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thực hiện thao tác này.");

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var review = await _restaurantRepository.LockReviewForReplyAsync(reviewId, ct);
        if (review == null)
        {
            throw new NotFoundException("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá.");
        }

        if (review.Restaurant.OwnerId != ownerId)
        {
            throw new ForbiddenException("FORBIDDEN", "Bạn không có quyền phản hồi đánh giá của nhà hàng khác.");
        }

        if (review.Status != ContentStatus.Visible)
        {
            throw new ConflictException("REVIEW_NOT_VISIBLE", "Đánh giá đang bị ẩn hoặc đã bị gỡ nên không thể phản hồi.");
        }

        var isNew = review.Reply == null;
        ReviewReply reply;

        if (isNew)
        {
            reply = new ReviewReply
            {
                ReviewId = review.Id,
                ReplierId = ownerId,
                Content = request.Content,
                Status = ContentStatus.Visible
            };
            _restaurantRepository.AddReviewReply(reply);
            review.Reply = reply;
        }
        else
        {
            reply = review.Reply!;
            reply.Content = request.Content;
        }

        if (isNew && review.UserId != ownerId)
        {
            var notification = new Domain.Entities.Notification
            {
                RecipientId = review.UserId,
                Type = "REVIEW_REPLIED",
                Title = "Phản hồi đánh giá",
                Body = $"{review.Restaurant.Name} đã phản hồi đánh giá của bạn",
                Data = JsonSerializer.Serialize(new { reviewId = review.Id, restaurantId = review.RestaurantId })
            };
            _restaurantRepository.AddNotification(notification);
        }

        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return RestaurantDetailMapper.MapReviewReply(reply);
    }
}
