using System.Globalization;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.UpdateBusinessHours;

/// <summary>Implements OWN-04 by replacing the owner's whole weekly schedule.</summary>
public sealed class UpdateBusinessHoursUseCase : IUpdateBusinessHoursUseCase
{
    private const string InvalidBusinessHoursCode = "INVALID_BUSINESS_HOURS";
    private const string InvalidBusinessHoursMessage = "Các khung giờ mở cửa không được chồng lên nhau.";

    private readonly IValidator<UpdateBusinessHoursRequestDto> _validator;
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public UpdateBusinessHoursUseCase(
        IValidator<UpdateBusinessHoursRequestDto> validator,
        IRestaurantRepository restaurantRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _restaurantRepository = restaurantRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<UpdateBusinessHoursResponseDto> ExecuteAsync(
        UpdateBusinessHoursRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        if (BusinessHourRules.HasOverlap(request.Hours))
        {
            throw new BadRequestException(InvalidBusinessHoursCode, InvalidBusinessHoursMessage);
        }

        var ownerId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để cập nhật giờ mở cửa.");
        var restaurant = await _restaurantRepository.GetTrackedWithBusinessHoursAsync(ownerId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");

        ReplaceHours(restaurant, request.Hours!);
        await _unitOfWork.SaveChangesAsync(ct);

        return new UpdateBusinessHoursResponseDto
        {
            Hours = restaurant.BusinessHours
                .OrderBy(hour => hour.DayOfWeek)
                .ThenBy(hour => hour.OpenTime)
                .Select(hour => new BusinessHourDto
                {
                    DayOfWeek = hour.DayOfWeek,
                    OpenTime = hour.OpenTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    CloseTime = hour.CloseTime.ToString("HH:mm", CultureInfo.InvariantCulture)
                })
                .ToArray(),
            IsOpenNow = OwnerRestaurantMapper.IsOpenNow(restaurant.BusinessHours, _timeProvider)
        };
    }

    /// <summary>
    /// Makes the tracked hours equal to the request. Rows are matched on (day, open time), the unique key of
    /// restaurant_business_hours: a kept key is updated in place, so one SaveChanges never deletes and re-inserts the
    /// same key. Removed rows are deleted as orphans of the required relationship.
    /// </summary>
    private static void ReplaceHours(Domain.Entities.Restaurant restaurant, IReadOnlyList<BusinessHourDto> requested)
    {
        var wanted = requested.ToDictionary(
            hour => (hour.DayOfWeek, Open: ParseTime(hour.OpenTime)),
            hour => ParseTime(hour.CloseTime));

        foreach (var existing in restaurant.BusinessHours.ToList())
        {
            if (wanted.Remove((existing.DayOfWeek, existing.OpenTime), out var closeTime))
            {
                existing.CloseTime = closeTime;
            }
            else
            {
                restaurant.BusinessHours.Remove(existing);
            }
        }

        foreach (var ((dayOfWeek, openTime), closeTime) in wanted)
        {
            restaurant.BusinessHours.Add(new RestaurantBusinessHour
            {
                RestaurantId = restaurant.Id,
                DayOfWeek = dayOfWeek,
                OpenTime = openTime,
                CloseTime = closeTime
            });
        }
    }

    /// <summary>Parses a time the validator has already checked.</summary>
    private static TimeOnly ParseTime(string value) =>
        BusinessHourRules.TryParseTime(value, out var time)
            ? time
            : throw new InvalidOperationException($"Unvalidated business hour '{value}'.");
}
