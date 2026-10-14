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

namespace ANGI.Application.UseCases.Restaurant.SubmitVerification;

/// <summary>Implements OWN-06 by creating the owner's first restaurant verification submission.</summary>
public sealed class SubmitVerificationUseCase : ISubmitVerificationUseCase
{
    private readonly IValidator<SubmitVerificationRequestDto> _validator;
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICloudinaryService _cloudinaryService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes the verification workflow and its persistence and media dependencies.</summary>
    public SubmitVerificationUseCase(
        IValidator<SubmitVerificationRequestDto> validator,
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

    /// <summary>Validates state and document ownership, then atomically creates the pending submission.</summary>
    public async Task<VerificationDto> ExecuteAsync(
        SubmitVerificationRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var ownerId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để nộp hồ sơ xác minh.");

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var restaurant = await _restaurantRepository.LockOwnerRestaurantForVerificationAsync(ownerId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");

        EnsureSubmissionState(restaurant.VerificationStatus);

        var mediaIds = request.Documents!.Select(document => document.MediaId).Distinct().ToArray();
        var ownedMedia = await _mediaRepository.GetOwnedByIdsAsync(ownerId, mediaIds, ct);
        var mediaById = RestaurantVerificationRules.EnsureVerificationMedia(request.Documents!, ownedMedia);

        var verification = new RestaurantVerification
        {
            RestaurantId = restaurant.Id,
            SubmittedBy = ownerId,
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
    /// Allows only a restaurant that was never submitted. A rejected restaurant (its last submission was rejected or
    /// needs more information) resubmits through OWN-08 instead.
    /// </summary>
    private static void EnsureSubmissionState(RestaurantVerificationStatus status)
    {
        switch (status)
        {
            case RestaurantVerificationStatus.Unverified:
                return;
            case RestaurantVerificationStatus.Verified:
                throw new ConflictException(
                    "RESTAURANT_ALREADY_VERIFIED",
                    "Nhà hàng đã được xác minh.");
            case RestaurantVerificationStatus.Rejected:
                throw new ConflictException(
                    "VERIFICATION_RESUBMIT_REQUIRED",
                    "Hồ sơ trước đã bị từ chối hoặc cần bổ sung. Vui lòng nộp lại hồ sơ.");
            default:
                throw new ConflictException(
                    "VERIFICATION_ALREADY_PENDING",
                    "Nhà hàng đã có hồ sơ xác minh đang chờ duyệt.");
        }
    }
}
