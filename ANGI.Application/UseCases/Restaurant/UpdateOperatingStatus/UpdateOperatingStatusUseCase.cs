using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.UpdateOperatingStatus;

/// <summary>Implements OWN-05: the owner opens or closes the restaurant.</summary>
public sealed class UpdateOperatingStatusUseCase : IUpdateOperatingStatusUseCase
{
    private static readonly IReadOnlyDictionary<string, RestaurantOperatingStatus> Statuses =
        Enum.GetValues<RestaurantOperatingStatus>()
            .ToDictionary(ToApiValue, status => status, StringComparer.Ordinal);

    private readonly IValidator<UpdateOperatingStatusRequestDto> _validator;
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuditLogService _auditLogService;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateOperatingStatusUseCase(
        IValidator<UpdateOperatingStatusRequestDto> validator,
        IRestaurantRepository restaurantRepository,
        ICurrentUserService currentUserService,
        IAuditLogService auditLogService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _restaurantRepository = restaurantRepository;
        _currentUserService = currentUserService;
        _auditLogService = auditLogService;
        _unitOfWork = unitOfWork;
    }

    public async Task<UpdateOperatingStatusResponseDto> ExecuteAsync(
        UpdateOperatingStatusRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var ownerId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để đổi trạng thái hoạt động.");
        var restaurant = await _restaurantRepository.GetTrackedByOwnerIdAsync(ownerId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");

        var oldStatus = restaurant.OperatingStatus;
        var newStatus = Statuses[request.OperatingStatus!];

        // A suspended restaurant may still close, but only a Mod can let it open again (API Design, OWN-05).
        if (newStatus == RestaurantOperatingStatus.Open &&
            oldStatus != RestaurantOperatingStatus.Open &&
            restaurant.ModerationStatus == RestaurantModerationStatus.Suspended)
        {
            throw new ConflictException(
                "RESTAURANT_SUSPENDED",
                "Nhà hàng đang bị đình chỉ nên chưa thể mở lại.");
        }

        if (newStatus != oldStatus)
        {
            restaurant.OperatingStatus = newStatus;
            _auditLogService.Add(new AuditLogEntry
            {
                Action = AuditActions.RestaurantOperatingStatusChanged,
                EntityType = AuditEntityTypes.Restaurant,
                EntityId = restaurant.Id,
                SubjectUserId = ownerId,
                OldValues = new { OperatingStatus = oldStatus },
                NewValues = new { OperatingStatus = newStatus, Reason = NormalizeOptional(request.Note) }
            });
            await _unitOfWork.SaveChangesAsync(ct);
        }

        return new UpdateOperatingStatusResponseDto { OperatingStatus = ToApiValue(restaurant.OperatingStatus) };
    }

    private static string ToApiValue(RestaurantOperatingStatus status) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
