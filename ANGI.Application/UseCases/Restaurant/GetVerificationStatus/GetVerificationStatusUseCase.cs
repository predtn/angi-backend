using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.UseCases.Restaurant.GetVerificationStatus;

/// <summary>Implements OWN-07 by returning the owner restaurant's verification status and history.</summary>
public sealed class GetVerificationStatusUseCase : IGetVerificationStatusUseCase
{
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICloudinaryService _cloudinaryService;

    /// <summary>Initializes the verification status query dependencies.</summary>
    public GetVerificationStatusUseCase(
        IRestaurantRepository restaurantRepository,
        ICurrentUserService currentUserService,
        ICloudinaryService cloudinaryService)
    {
        _restaurantRepository = restaurantRepository;
        _currentUserService = currentUserService;
        _cloudinaryService = cloudinaryService;
    }

    /// <summary>Loads and maps the owner restaurant's verification status and submission history.</summary>
    public async Task<GetVerificationStatusResponseDto> ExecuteAsync(CancellationToken ct)
    {
        var ownerId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem trạng thái xác minh.");

        var restaurant = await _restaurantRepository.GetOwnerRestaurantWithVerificationsAsync(ownerId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");

        var items = restaurant.Verifications
            .OrderByDescending(item => item.SubmittedAt)
            .ThenByDescending(item => item.Id)
            .Select(verification => RestaurantVerificationMapper.Map(verification, _cloudinaryService))
            .ToArray();

        return new GetVerificationStatusResponseDto
        {
            VerificationStatus = ToApiValue(restaurant.VerificationStatus),
            Items = items
        };
    }

    /// <summary>Converts a domain enum to the snake_case value documented by the API.</summary>
    private static string ToApiValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
