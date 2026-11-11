using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.UpdateDishModerationStatus;

/// <summary>Implements MOD-R4 while allowing PostgreSQL triggers to create reco outbox events.</summary>
public sealed class UpdateDishModerationStatusUseCase(IValidator<UpdateDishModerationStatusRequestDto> validator,
    ModerationAccess access, IRestaurantModerationRepository repository, IUnitOfWork unitOfWork) : IUpdateDishModerationStatusUseCase
{
    /// <summary>Locks parent then dish, rejects removed dishes, and saves the requested active or hidden state.</summary>
    public async Task<UpdateDishModerationStatusResponseDto> ExecuteAsync(int dishId,
        UpdateDishModerationStatusRequestDto request, CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await validator.ValidateAndThrowAsync(request, ct);
        // STEP-02: Check the caller's permission through access.RequireAsync.
        await access.RequireAsync(ct, "RESTAURANT_MODERATE");
        // STEP-03: Prepare restaurantId from repository.GetDishRestaurantIdAsync.
        var restaurantId = await repository.GetDishRestaurantIdAsync(dishId, ct)
            ?? throw new NotFoundException("DISH_NOT_FOUND", "Không tìm thấy món ăn.");
        // STEP-04: Open the database transaction.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        // STEP-05: Reject with DISH_NOT_FOUND when repository.LockRestaurantAsync() is missing.
        if (await repository.LockRestaurantAsync(restaurantId, ct) is null)
            throw new NotFoundException("DISH_NOT_FOUND", "Không tìm thấy món ăn.");
        // STEP-06: Prepare dish from repository.LockDishAsync.
        var dish = await repository.LockDishAsync(dishId, ct)
            ?? throw new NotFoundException("DISH_NOT_FOUND", "Không tìm thấy món ăn.");
        // STEP-07: Reject with DISH_REMOVED when dish.Status is DishStatus.Removed.
        if (dish.Status == DishStatus.Removed)
            throw new ConflictException("DISH_REMOVED", "Món ăn đã được gỡ khỏi menu.");
        // STEP-08: Update dish: Status.
        dish.Status = request.Status == "active" ? DishStatus.Active : DishStatus.Hidden;
        // STEP-09: Save the pending changes.
        await unitOfWork.SaveChangesAsync(ct);
        // STEP-10: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-11: Return the new result.
        return new() { DishId = dish.Id, Status = ModerationMapper.Api(dish.Status) };
    }
}
