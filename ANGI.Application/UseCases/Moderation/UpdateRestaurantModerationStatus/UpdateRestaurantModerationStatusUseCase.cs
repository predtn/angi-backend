using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Moderation.UpdateRestaurantModerationStatus;

/// <summary>Implements MOD-R3 with atomic visibility, audit and notification writes.</summary>
public sealed class UpdateRestaurantModerationStatusUseCase(IValidator<UpdateRestaurantModerationStatusRequestDto> validator,
    ModerationAccess access, IRestaurantModerationRepository repository, IUnitOfWork unitOfWork,
    RestaurantModerationEffects effects) : IUpdateRestaurantModerationStatusUseCase
{
    /// <summary>Locks the restaurant, checks an optional report association, and persists the requested state.</summary>
    public async Task<UpdateRestaurantModerationStatusResponseDto> ExecuteAsync(long restaurantId,
        UpdateRestaurantModerationStatusRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "RESTAURANT_MODERATE");
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var restaurant = await repository.LockRestaurantAsync(restaurantId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
        if (request.ReportId.HasValue)
        {
            var target = await repository.GetReportTargetAsync(request.ReportId.Value, ct);
            if (target is null || target.Type != ReportTargetType.Restaurant || target.Id != restaurantId)
                throw new ValidationException([new ValidationFailure(nameof(request.ReportId), "Báo cáo không thuộc nhà hàng này.")]);
        }
        var status = request.ModerationStatus switch
        {
            "visible" => RestaurantModerationStatus.Visible,
            "hidden" => RestaurantModerationStatus.Hidden,
            _ => RestaurantModerationStatus.Suspended
        };
        effects.Apply(restaurant, status, request.Reason.Trim(), request.ReportId);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new() { RestaurantId = restaurant.Id, ModerationStatus = ModerationMapper.Api(restaurant.ModerationStatus) };
    }
}
