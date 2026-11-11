using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Account;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Account;
using ANGI.Application.DTOs.Account;

namespace ANGI.Application.UseCases.Account.GetMyProfile;

/// <summary>Implements ME-01 by loading personal profile, external logins, permissions, and preference status.</summary>
public sealed class GetMyProfileUseCase : IGetMyProfileUseCase
{
    private const string TravelerRole = "TRAVELER";
    private const string RestaurantOwnerRole = "RESTAURANT_OWNER";

    private readonly ICurrentUserService _currentUserService;
    private readonly IAccountRepository _accountRepository;
    private readonly IRecommendationService _recommendationService;

    public GetMyProfileUseCase(
        ICurrentUserService currentUserService,
        IAccountRepository accountRepository,
        IRecommendationService recommendationService)
    {
        _currentUserService = currentUserService;
        _accountRepository = accountRepository;
        _recommendationService = recommendationService;
    }

    public async Task<UserProfileDto> ExecuteAsync(CancellationToken ct)
    {
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem thông tin tài khoản.");

        var user = await _accountRepository.GetProfileUserAsync(userId, ct)
            ?? throw new NotFoundException("USER_NOT_FOUND", "Không tìm thấy người dùng.");

        var linkedProviders = await _accountRepository.GetLinkedProvidersAsync(userId, ct);

        long? restaurantId = null;
        if (string.Equals(user.Role.Code, RestaurantOwnerRole, StringComparison.OrdinalIgnoreCase))
        {
            restaurantId = await _accountRepository.GetOwnerRestaurantIdAsync(userId, ct);
        }

        var preferenceStatus = await ResolvePreferenceStatusAsync(user.Id, user.Role.Code, ct);
        var permissions = await _accountRepository.GetEffectivePermissionsAsync(userId, user.Role.Code, ct);

        return AccountMapper.Map(
            user,
            user.AvatarMedia?.Url,
            linkedProviders,
            preferenceStatus,
            restaurantId,
            permissions);
    }

    private async Task<string?> ResolvePreferenceStatusAsync(int userId, string roleCode, CancellationToken ct)
    {
        if (!string.Equals(roleCode, TravelerRole, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var surveyStatus = await _recommendationService.GetSurveyStatusAsync(userId, ct);
        if (surveyStatus is null || !surveyStatus.Completed)
        {
            return null;
        }

        return surveyStatus.AnswerCount > 0 ? "completed" : "skipped";
    }
}
