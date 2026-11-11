using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.ResubmitVerification;

/// <summary>Implements OWN-08 by creating a new pending submission that points back at a rejected one.</summary>
public sealed class ResubmitVerificationUseCase : IResubmitVerificationUseCase
{
    private readonly IValidator<ResubmitVerificationRequestDto> _validator;
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICloudinaryService _cloudinaryService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes the resubmission workflow and its persistence and media dependencies.</summary>
    public ResubmitVerificationUseCase(
        IValidator<ResubmitVerificationRequestDto> validator,
        IRestaurantRepository restaurantRepository,
        IMediaRepository mediaRepository,
        ICurrentUserService currentUserService,
        ICloudinaryService cloudinaryService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _restaurantRepository = restaurantRepository;
        _mediaRepository = mediaRepository;
        _currentUserService = currentUserService;
        _cloudinaryService = cloudinaryService;
        _unitOfWork = unitOfWork;
    }

    /// <summary>
    /// Verifies the previous submission belongs to the caller's restaurant, is the latest one and is resubmittable,
    /// then atomically creates the new pending submission and marks the restaurant pending.
    /// </summary>
    public async Task<VerificationDto> ExecuteAsync(
        long verificationId,
        ResubmitVerificationRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var ownerId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để nộp lại hồ sơ xác minh.");

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var restaurant = await _restaurantRepository.LockOwnerRestaurantForVerificationAsync(ownerId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");

        // Scoped to the caller's restaurant: another owner's id is indistinguishable from a missing one.
        var previousStatus = await _restaurantRepository.GetVerificationStatusAsync(restaurant.Id, verificationId, ct)
            ?? throw new NotFoundException("VERIFICATION_NOT_FOUND", "Không tìm thấy hồ sơ xác minh.");

        await EnsureResubmittableAsync(restaurant, verificationId, previousStatus, ct);

        var mediaIds = request.Documents!.Select(document => document.MediaId).Distinct().ToArray();
        var ownedMedia = await _mediaRepository.GetOwnedByIdsAsync(ownerId, mediaIds, ct);
        var mediaById = RestaurantVerificationRules.EnsureVerificationMedia(request.Documents!, ownedMedia);

        var verification = new RestaurantVerification
        {
            RestaurantId = restaurant.Id,
            SubmittedBy = ownerId,
            PreviousId = verificationId,
            LegalName = request.LegalName!.Trim(),
            BusinessLicenseNo = request.BusinessLicenseNo!.Trim(),
            TaxCode = RestaurantVerificationRules.NormalizeOptional(request.TaxCode),
            OwnerNote = RestaurantVerificationRules.NormalizeOptional(request.OwnerNote),
            Status = VerificationStatus.Pending
        };

        RestaurantVerificationRules.AddDocuments(verification, request.Documents!);

        restaurant.VerificationStatus = RestaurantVerificationStatus.Pending;
        _restaurantRepository.AddVerification(verification);
        await _unitOfWork.SaveChangesAsync(ct);

        var storageKeys = mediaById.ToDictionary(item => item.Key, item => item.Value.StorageKey);
        var result = RestaurantVerificationMapper.Map(verification, _cloudinaryService, storageKeys);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Allows only a rejected or needs_more_info submission that is still the restaurant's latest, while the
    /// restaurant is neither pending nor verified.
    /// </summary>
    private async Task EnsureResubmittableAsync(
        ANGI.Domain.Entities.Restaurant restaurant,
        long verificationId,
        VerificationStatus previousStatus,
        CancellationToken ct)
    {
        var statusAllowsResubmit = previousStatus is VerificationStatus.Rejected or VerificationStatus.NeedsMoreInfo;
        var restaurantAllowsResubmit = restaurant.VerificationStatus is not
            (RestaurantVerificationStatus.Pending or RestaurantVerificationStatus.Verified);
        if (statusAllowsResubmit && restaurantAllowsResubmit)
        {
            var latestId = await _restaurantRepository.GetLatestVerificationIdAsync(restaurant.Id, ct);
            if (latestId == verificationId)
            {
                return;
            }
        }

        throw new ConflictException(
            "VERIFICATION_NOT_RESUBMITTABLE",
            "Chỉ có thể nộp lại hồ sơ mới nhất đã bị từ chối hoặc cần bổ sung thông tin.");
    }
}
