using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.UpdateDishAvailability;

/// <summary>Implements OWN-10 without creating recommendation outbox events.</summary>
public sealed class UpdateDishAvailabilityUseCase : IUpdateDishAvailabilityUseCase
{
    private readonly IValidator<UpdateDishAvailabilityRequestDto> _validator;
    private readonly IRestaurantMenuRepository _repository;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes availability validation, ownership, and persistence dependencies.</summary>
    public UpdateDishAvailabilityUseCase(
        IValidator<UpdateDishAvailabilityRequestDto> validator,
        IRestaurantMenuRepository repository,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _repository = repository;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Validates and persists the direct sold-out switch for an owned dish.</summary>
    public async Task<DishAvailabilityDto> ExecuteAsync(
        int dishId,
        UpdateDishAvailabilityRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để cập nhật món.");
        var dish = await _repository.GetTrackedOwnerDishAsync(ownerId, dishId, ct)
            ?? throw new NotFoundException("DISH_NOT_FOUND", "Không tìm thấy món ăn.");

        dish.IsAvailable = request.IsAvailable!.Value;
        await _unitOfWork.SaveChangesAsync(ct);
        return new DishAvailabilityDto { DishId = dish.Id, IsAvailable = dish.IsAvailable };
    }
}
