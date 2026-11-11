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
        // STEP-01: Prepare ownerId.
        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để tạo bản menu.");

        // STEP-02: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-03: Prepare restaurant from repository.LockOwnerRestaurantAsync.
        var restaurant = await _repository.LockOwnerRestaurantAsync(ownerId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
        // STEP-04: Reject with RESTAURANT_NOT_VERIFIED when restaurant.VerificationStatus is not Verified.
        if (restaurant.VerificationStatus != RestaurantVerificationStatus.Verified)
        {
            throw new ForbiddenException(
                "RESTAURANT_NOT_VERIFIED",
                "Nhà hàng chưa được xác minh nên chưa thể tạo menu.");
        }

        // STEP-05: Load openSubmission through repository.GetOpenSubmissionAsync.
        var openSubmission = await _repository.GetOpenSubmissionAsync(restaurant.Id, ct);
        // STEP-06: Throw SubmissionAlreadyOpenException when openSubmission exists.
        if (openSubmission is not null)
        {
            throw new SubmissionAlreadyOpenException(openSubmission.Id);
        }

        // STEP-07: Load dishes through repository.GetDraftSourceDishesAsync.
        var dishes = await _repository.GetDraftSourceDishesAsync(restaurant.Id, ct);
        // STEP-08: Prepare submission as MenuSubmission.
        var submission = new MenuSubmission
        {
            RestaurantId = restaurant.Id,
            CreatedBy = ownerId,
            Status = MenuSubmissionStatus.Draft
        };
        // STEP-09: For each dish in dishes, call submission.Items.Add.
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

        // STEP-10: Queue submission through repository.AddSubmission.
        _repository.AddSubmission(submission);
        // STEP-11: Save the pending changes.
        await _unitOfWork.SaveChangesAsync(ct);
        // STEP-12: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-13: Return the result of RestaurantMenuMapper.MapClonedSubmission.
        return RestaurantMenuMapper.MapClonedSubmission(submission, dishes);
    }
}
