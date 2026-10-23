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
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "RESTAURANT_MODERATE");
        var restaurantId = await repository.GetDishRestaurantIdAsync(dishId, ct)
            ?? throw new NotFoundException("DISH_NOT_FOUND", "Không tìm thấy món ăn.");
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        if (await repository.LockRestaurantAsync(restaurantId, ct) is null)
            throw new NotFoundException("DISH_NOT_FOUND", "Không tìm thấy món ăn.");
        var dish = await repository.LockDishAsync(dishId, ct)
            ?? throw new NotFoundException("DISH_NOT_FOUND", "Không tìm thấy món ăn.");
        if (dish.Status == DishStatus.Removed)
            throw new ConflictException("DISH_REMOVED", "Món ăn đã được gỡ khỏi menu.");
        dish.Status = request.Status == "active" ? DishStatus.Active : DishStatus.Hidden;
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new() { DishId = dish.Id, Status = ModerationMapper.Api(dish.Status) };
    }
}
