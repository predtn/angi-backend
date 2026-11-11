using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Roadmap.RateItem;

/// <summary>
/// Implements RM-13: one like or dislike per owned roadmap item. The feedback row and the dish counters change in one
/// transaction; the dish_feedbacks trigger writes FEEDBACK_CREATED to the outbox for RECO-04, so reco is not called.
/// </summary>
public sealed class RateRoadmapItemUseCase : IRateRoadmapItemUseCase
{
    /// <summary>Unique index on dish_feedbacks.roadmap_item_id (one feedback per item).</summary>
    public const string ItemFeedbackUniqueIndex = "ix_dish_feedbacks_roadmap_item_id";

    private readonly IValidator<RateRoadmapItemRequestDto> _validator;
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes validation, persistence, current-user, and transaction dependencies.</summary>
    public RateRoadmapItemUseCase(
        IValidator<RateRoadmapItemRequestDto> validator,
        IRoadmapRepository roadmapRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _roadmapRepository = roadmapRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
    }

    /// <summary>
    /// Locks the owned item, stores the feedback with the item's reco request id, then updates the dish counters.
    /// The dish may have been hidden or removed since it was added: the traveler still tried it, so it can be rated.
    /// </summary>
    public async Task<RateRoadmapItemResponseDto> ExecuteAsync(
        long roadmapId,
        long itemId,
        RateRoadmapItemRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để đánh giá món.");
        var value = request.Value == RateRoadmapItemRequestDtoValidator.Like ? FeedbackValue.Like : FeedbackValue.Dislike;

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var item = await _roadmapRepository.LockAndGetOwnedItemForFeedbackAsync(roadmapId, itemId, userId, ct)
            ?? throw new NotFoundException("ROADMAP_ITEM_NOT_FOUND", "Không tìm thấy món trong roadmap.");
        if (await _roadmapRepository.ItemHasFeedbackAsync(item.Id, ct))
        {
            throw FeedbackAlreadyExists();
        }

        var feedback = new DishFeedback
        {
            UserId = userId,
            DishId = item.DishId,
            Value = value,
            RoadmapItemId = item.Id,
            RecoRequestId = item.RecoRequestId
        };
        _roadmapRepository.AddFeedback(feedback);
        try
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (UniqueConstraintViolationException exception) when (
            string.Equals(exception.ConstraintName, ItemFeedbackUniqueIndex, StringComparison.Ordinal))
        {
            // The item lock serializes RM-13 calls; the unique index stays authoritative for any other writer.
            throw FeedbackAlreadyExists();
        }

        var counts = await _roadmapRepository.IncrementDishFeedbackCountAsync(item.DishId, value, ct);
        await transaction.CommitAsync(ct);

        return new RateRoadmapItemResponseDto
        {
            FeedbackId = feedback.Id,
            Value = request.Value,
            DishId = item.DishId,
            LikeCount = counts.LikeCount,
            DislikeCount = counts.DislikeCount,
            CreatedAt = feedback.CreatedAt
        };
    }

    private static ConflictException FeedbackAlreadyExists() =>
        new("FEEDBACK_ALREADY_EXISTS", "Món này đã được đánh giá.");
}
