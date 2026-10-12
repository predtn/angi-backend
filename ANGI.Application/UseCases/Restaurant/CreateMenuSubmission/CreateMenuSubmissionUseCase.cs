using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Restaurant.CreateMenuSubmission;

/// <summary>Implements OWN-11 by cloning the current menu into a new draft.</summary>
public sealed class CreateMenuSubmissionUseCase : ICreateMenuSubmissionUseCase
{
    private readonly IRestaurantMenuRepository _repository;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes the serialized draft-creation workflow.</summary>
    public CreateMenuSubmissionUseCase(
        IRestaurantMenuRepository repository,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Locks the restaurant, enforces workflow state, and copies every current dish.</summary>
    public async Task<MenuSubmissionWithItemsDto> ExecuteAsync(CancellationToken ct)
    {
        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để tạo bản menu.");

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var restaurant = await _repository.LockOwnerRestaurantAsync(ownerId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
        if (restaurant.VerificationStatus != RestaurantVerificationStatus.Verified)
        {
            throw new ForbiddenException(
                "RESTAURANT_NOT_VERIFIED",
                "Nhà hàng chưa được xác minh nên chưa thể tạo menu.");
        }

        var openSubmission = await _repository.GetOpenSubmissionAsync(restaurant.Id, ct);
        if (openSubmission is not null)
        {
            throw new SubmissionAlreadyOpenException(openSubmission.Id);
        }

        var dishes = await _repository.GetDraftSourceDishesAsync(restaurant.Id, ct);
        var submission = new MenuSubmission
        {
            RestaurantId = restaurant.Id,
            CreatedBy = ownerId,
            Status = MenuSubmissionStatus.Draft
        };
        foreach (var dish in dishes)
        {
            submission.Items.Add(new MenuSubmissionItem
            {
                SourceDishId = dish.Id,
                Name = dish.Name,
                Description = dish.Description,
                DishForm = dish.DishForm,
                Price = dish.Price,
                CoverMediaId = dish.CoverMediaId,
                TagIds = dish.DishTags.Select(tag => tag.TagId).OrderBy(id => id).ToArray(),
                SortOrder = dish.SortOrder
            });
        }

        _repository.AddSubmission(submission);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return RestaurantMenuMapper.MapClonedSubmission(submission, dishes);
    }
}
