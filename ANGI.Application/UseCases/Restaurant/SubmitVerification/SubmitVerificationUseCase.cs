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
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Restaurant.SubmitVerification;

/// <summary>Implements OWN-06 by creating the owner's first restaurant verification submission.</summary>
public sealed class SubmitVerificationUseCase : ISubmitVerificationUseCase
{
    private static readonly IReadOnlyDictionary<string, VerificationDocumentType> DocumentTypes =
        new Dictionary<string, VerificationDocumentType>(StringComparer.Ordinal)
        {
            ["business_license"] = VerificationDocumentType.BusinessLicense,
            ["food_safety_cert"] = VerificationDocumentType.FoodSafetyCert,
            ["id_card"] = VerificationDocumentType.IdCard,
            ["storefront_photo"] = VerificationDocumentType.StorefrontPhoto,
            ["other"] = VerificationDocumentType.Other
        };

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
        var mediaById = EnsureVerificationMedia(request, ownedMedia);

        var verification = new RestaurantVerification
        {
            RestaurantId = restaurant.Id,
            SubmittedBy = ownerId,
            LegalName = request.LegalName!.Trim(),
            BusinessLicenseNo = request.BusinessLicenseNo!.Trim(),
            TaxCode = NormalizeOptional(request.TaxCode),
            OwnerNote = NormalizeOptional(request.OwnerNote),
            Status = VerificationStatus.Pending
        };

        foreach (var document in request.Documents!)
        {
            verification.Documents.Add(new RestaurantVerificationDocument
            {
                DocType = DocumentTypes[document.DocType!],
                // Only the id: the media rows were read untracked, so setting the navigation would make EF insert
                // them again (duplicate pk_media_files).
                MediaId = document.MediaId
            });
        }

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

    /// <summary>Ensures every media id belongs to the caller and came from a private verification upload.</summary>
    private static IReadOnlyDictionary<long, MediaFile> EnsureVerificationMedia(
        SubmitVerificationRequestDto request,
        IReadOnlyCollection<MediaFile> ownedMedia)
    {
        var mediaById = ownedMedia.ToDictionary(media => media.Id);
        var failures = new List<ValidationFailure>();
        var documents = request.Documents!;
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            if (!mediaById.TryGetValue(document.MediaId, out var media) || !IsVerificationUpload(media.StorageKey))
            {
                failures.Add(new ValidationFailure(
                    $"Documents[{index}].MediaId",
                    "Tệp không tồn tại, không thuộc tài khoản của bạn hoặc không được tải lên cho hồ sơ xác minh."));
            }
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        return mediaById;
    }

    /// <summary>Recognizes the private storage-key layout produced by MEDIA-01 for verification documents.</summary>
    private static bool IsVerificationUpload(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return false;
        }

        var parts = storageKey.Split('/', 4, StringSplitOptions.None);
        return parts.Length == 4 &&
            parts[0] is "image" or "raw" &&
            parts[1] == "authenticated" &&
            parts[2] == "verification_doc" &&
            !string.IsNullOrWhiteSpace(parts[3]);
    }

    /// <summary>Trims optional text and converts blank values to null.</summary>
    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
