using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Account;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Account;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Account;
using FluentValidation;

namespace ANGI.Application.UseCases.Account.UpdateMyProfile;

/// <summary>Implements ME-02 by partially updating personal profile fields and recording audit changes.</summary>
public sealed class UpdateMyProfileUseCase : IUpdateMyProfileUseCase
{
    private const string TravelerRole = "TRAVELER";
    private const string RestaurantOwnerRole = "RESTAURANT_OWNER";

    private static readonly IReadOnlySet<string> AllowedImageMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    private readonly IValidator<UpdateProfileRequestDto> _validator;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAccountRepository _accountRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly IRecommendationService _recommendationService;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateMyProfileUseCase(
        IValidator<UpdateProfileRequestDto> validator,
        ICurrentUserService currentUserService,
        IAccountRepository accountRepository,
        IMediaRepository mediaRepository,
        IAuditLogService auditLogService,
        IRecommendationService recommendationService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _currentUserService = currentUserService;
        _accountRepository = accountRepository;
        _mediaRepository = mediaRepository;
        _auditLogService = auditLogService;
        _recommendationService = recommendationService;
        _unitOfWork = unitOfWork;
    }

    public async Task<UserProfileDto> ExecuteAsync(UpdateProfileRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để cập nhật thông tin tài khoản.");

        var user = await _accountRepository.GetUserForUpdateAsync(userId, ct)
            ?? throw new NotFoundException("USER_NOT_FOUND", "Không tìm thấy người dùng.");

        string? updatedAvatarUrl = user.AvatarMedia?.Url;
        if (request.AvatarMediaIdSpecified)
        {
            if (request.AvatarMediaId.HasValue)
            {
                var mediaList = await _mediaRepository.GetOwnedByIdsAsync(
                    userId,
                    [request.AvatarMediaId.Value],
                    ct);

                if (mediaList.Count == 0 || !IsAllowedAvatarImage(mediaList[0].MimeType))
                {
                    throw new NotFoundException(
                        "MEDIA_NOT_FOUND",
                        "Không tìm thấy tệp ảnh đại diện hoặc tệp không hợp lệ.");
                }

                updatedAvatarUrl = mediaList[0].Url;
            }
            else
            {
                updatedAvatarUrl = null;
            }
        }

        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();

        ApplyScalarChanges(user, request, oldValues, newValues);

        if (oldValues.Count > 0)
        {
            _auditLogService.Add(new AuditLogEntry
            {
                Action = AuditActions.ProfileUpdated,
                EntityType = AuditEntityTypes.User,
                EntityId = user.Id,
                SubjectUserId = user.Id,
                OldValues = oldValues,
                NewValues = newValues
            });

            await _unitOfWork.SaveChangesAsync(ct);
        }

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
            updatedAvatarUrl,
            linkedProviders,
            preferenceStatus,
            restaurantId,
            permissions);
    }

    private static void ApplyScalarChanges(
        Domain.Entities.User user,
        UpdateProfileRequestDto request,
        IDictionary<string, object?> oldValues,
        IDictionary<string, object?> newValues)
    {
        if (request.DisplayNameSpecified)
        {
            var normalizedName = request.DisplayName!.Trim();
            if (!string.Equals(user.DisplayName, normalizedName, StringComparison.Ordinal))
            {
                oldValues["display_name"] = user.DisplayName;
                newValues["display_name"] = normalizedName;
                user.DisplayName = normalizedName;
            }
        }

        if (request.PhoneSpecified)
        {
            var normalizedPhone = NormalizeOptional(request.Phone);
            if (!string.Equals(user.Phone, normalizedPhone, StringComparison.Ordinal))
            {
                oldValues["phone"] = user.Phone;
                newValues["phone"] = normalizedPhone;
                user.Phone = normalizedPhone;
            }
        }

        if (request.BioSpecified)
        {
            var normalizedBio = NormalizeOptional(request.Bio);
            if (!string.Equals(user.Bio, normalizedBio, StringComparison.Ordinal))
            {
                oldValues["bio"] = user.Bio;
                newValues["bio"] = normalizedBio;
                user.Bio = normalizedBio;
            }
        }

        if (request.AvatarMediaIdSpecified && user.AvatarMediaId != request.AvatarMediaId)
        {
            oldValues["avatar_media_id"] = user.AvatarMediaId;
            newValues["avatar_media_id"] = request.AvatarMediaId;
            user.AvatarMediaId = request.AvatarMediaId;
        }
    }

    private static bool IsAllowedAvatarImage(string? mimeType) =>
        !string.IsNullOrWhiteSpace(mimeType) && AllowedImageMimeTypes.Contains(mimeType.Trim());

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
