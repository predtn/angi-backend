using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using FluentValidation;
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Restaurant.UpdateOwnerProfile;

/// <summary>Implements OWN-03 by applying a validated partial restaurant update.</summary>
public sealed class UpdateOwnerRestaurantUseCase : IUpdateOwnerRestaurantUseCase
{
    private static readonly IReadOnlySet<string> _allowedImageMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    private readonly IValidator<UpdateRestaurantRequestDto> _validator;
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuditLogService _auditLogService;
    private readonly ICloudinaryService _cloudinaryService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes validation, persistence, audit, and response mapping dependencies.</summary>
    public UpdateOwnerRestaurantUseCase(
        IValidator<UpdateRestaurantRequestDto> validator,
        IRestaurantRepository restaurantRepository,
        IMediaRepository mediaRepository,
        ICurrentUserService currentUserService,
        IAuditLogService auditLogService,
        ICloudinaryService cloudinaryService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _restaurantRepository = restaurantRepository;
        _mediaRepository = mediaRepository;
        _currentUserService = currentUserService;
        _auditLogService = auditLogService;
        _cloudinaryService = cloudinaryService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Validates media ownership, updates changed fields, and writes one atomic audit row.</summary>
    public async Task<OwnerRestaurantDto> ExecuteAsync(UpdateRestaurantRequestDto request, CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare ownerId and restaurant, then load requestedMedia through GetValidOwnedMediaAsync.
        var ownerId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để cập nhật nhà hàng.");
        var restaurant = await _restaurantRepository.GetTrackedOwnerProfileAsync(ownerId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");

        var requestedMedia = await GetValidOwnedMediaAsync(request, ownerId, ct);

        // STEP-03: Prepare oldValues and newValues, then call ApplyScalarChanges and ApplyImageChanges.
        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();
        ApplyScalarChanges(restaurant, request, oldValues, newValues);
        ApplyImageChanges(restaurant, request, oldValues, newValues);

        // STEP-04: When oldValues.Count > 0, call auditLogService.Add and call unitOfWork.SaveChangesAsync.
        if (oldValues.Count > 0)
        {
            _auditLogService.Add(new AuditLogEntry
            {
                Action = AuditActions.RestaurantUpdated,
                EntityType = AuditEntityTypes.Restaurant,
                EntityId = restaurant.Id,
                SubjectUserId = ownerId,
                OldValues = oldValues,
                NewValues = newValues
            });
            await _unitOfWork.SaveChangesAsync(ct);
        }

        // STEP-05: Prepare requestedMediaUrls through requestedMedia.ToDictionary.
        var requestedMediaUrls = requestedMedia.ToDictionary(media => media.Id, media => media.Url);
        // STEP-06: Return the result of OwnerRestaurantMapper.Map.
        return OwnerRestaurantMapper.Map(restaurant, _cloudinaryService, _timeProvider, requestedMediaUrls);
    }

    /// <summary>Loads requested media and verifies that every reference is an owner-uploaded image.</summary>
    private async Task<IReadOnlyList<MediaFile>> GetValidOwnedMediaAsync(
        UpdateRestaurantRequestDto request,
        int ownerId,
        CancellationToken ct)
    {
        // STEP-01: Prepare requestedIds as HashSet<long>.
        var requestedIds = new HashSet<long>();
        // STEP-02: When request.CoverMediaIdSpecified is true and request.CoverMediaId has a value, call requestedIds.Add.
        if (request.CoverMediaIdSpecified && request.CoverMediaId.HasValue)
        {
            requestedIds.Add(request.CoverMediaId.Value);
        }

        // STEP-03: When request.ImageMediaIdsSpecified is true and request.ImageMediaIds exists, call requestedIds.UnionWith.
        if (request.ImageMediaIdsSpecified && request.ImageMediaIds is not null)
        {
            requestedIds.UnionWith(request.ImageMediaIds);
        }

        // STEP-04: Return the result of Array.Empty early when requestedIds.Count is 0.
        if (requestedIds.Count == 0)
        {
            return Array.Empty<MediaFile>();
        }

        // STEP-05: Load owned through mediaRepository.GetOwnedByIdsAsync.
        var owned = await _mediaRepository.GetOwnedByIdsAsync(ownerId, requestedIds, ct);
        // STEP-06: Prepare failures as List<ValidationFailure> and imageIds through Where(filtered).Select(filtered).ToHashSet.
        var failures = new List<ValidationFailure>();
        var imageIds = owned
            .Where(media => IsAllowedImage(media.MimeType))
            .Select(media => media.Id)
            .ToHashSet();
        // STEP-07: When request.CoverMediaId exists and imageIds does not contain coverId, call failures.Add.
        if (request.CoverMediaId is { } coverId && !imageIds.Contains(coverId))
        {
            failures.Add(new ValidationFailure(
                nameof(request.CoverMediaId),
                "Ảnh bìa không tồn tại, không thuộc tài khoản của bạn hoặc không phải ảnh hợp lệ."));
        }

        // STEP-08: Prepare missingGallery through Empty<long>().Where(filtered).ToArray.
        var missingGallery = (request.ImageMediaIds ?? Array.Empty<long>())
            .Where(id => !imageIds.Contains(id))
            .ToArray();
        // STEP-09: When missingGallery.Length > 0, call failures.Add.
        if (missingGallery.Length > 0)
        {
            failures.Add(new ValidationFailure(
                nameof(request.ImageMediaIds),
                "Một hoặc nhiều tệp không tồn tại, không thuộc tài khoản của bạn hoặc không phải ảnh hợp lệ."));
        }

        // STEP-10: Throw ValidationException when failures.Count > 0.
        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        // STEP-11: Return owned.
        return owned;
    }

    /// <summary>Accepts only the image formats supported by MEDIA-01, regardless of MIME casing.</summary>
    private static bool IsAllowedImage(string? mimeType) =>
        !string.IsNullOrWhiteSpace(mimeType) && _allowedImageMimeTypes.Contains(mimeType.Trim());

    /// <summary>Applies supplied scalar fields and records only values that actually changed.</summary>
    private static void ApplyScalarChanges(
        Domain.Entities.Restaurant restaurant,
        UpdateRestaurantRequestDto request,
        IDictionary<string, object?> oldValues,
        IDictionary<string, object?> newValues)
    {
        Apply(request.NameSpecified, "name", restaurant.Name, request.Name?.Trim(), value => restaurant.Name = value!, oldValues, newValues);
        Apply(request.DescriptionSpecified, "description", restaurant.Description, NormalizeOptional(request.Description), value => restaurant.Description = value, oldValues, newValues);
        Apply(request.PhoneSpecified, "phone", restaurant.Phone, request.Phone?.Trim(), value => restaurant.Phone = value, oldValues, newValues);
        Apply(request.EmailSpecified, "email", restaurant.Email, NormalizeOptional(request.Email)?.ToLowerInvariant(), value => restaurant.Email = value, oldValues, newValues);
        Apply(request.WebsiteSpecified, "website", restaurant.Website, NormalizeOptional(request.Website), value => restaurant.Website = value, oldValues, newValues);
        Apply(request.AddressLineSpecified, "address_line", restaurant.AddressLine, request.AddressLine?.Trim(), value => restaurant.AddressLine = value!, oldValues, newValues);
        Apply(request.WardSpecified, "ward", restaurant.Ward, NormalizeOptional(request.Ward), value => restaurant.Ward = value, oldValues, newValues);
        Apply(request.ProvinceNameSpecified, "province_name", restaurant.ProvinceName, request.ProvinceName?.Trim(), value => restaurant.ProvinceName = value, oldValues, newValues);
        Apply(request.LatitudeSpecified, "latitude", (decimal?)restaurant.Latitude, request.Latitude, value => restaurant.Latitude = value!.Value, oldValues, newValues);
        Apply(request.LongitudeSpecified, "longitude", (decimal?)restaurant.Longitude, request.Longitude, value => restaurant.Longitude = value!.Value, oldValues, newValues);
        Apply(request.PriceLevelSpecified, "price_level", restaurant.PriceLevel, request.PriceLevel, value => restaurant.PriceLevel = value, oldValues, newValues);
        Apply(request.CoverMediaIdSpecified, "cover_media_id", restaurant.CoverMediaId, request.CoverMediaId, value => restaurant.CoverMediaId = value, oldValues, newValues);
    }

    /// <summary>Replaces gallery join rows when the ordered image identifier list changed.</summary>
    private static void ApplyImageChanges(
        Domain.Entities.Restaurant restaurant,
        UpdateRestaurantRequestDto request,
        IDictionary<string, object?> oldValues,
        IDictionary<string, object?> newValues)
    {
        // STEP-01: Stop here when request.ImageMediaIdsSpecified is false or request.ImageMediaIds is missing.
        if (!request.ImageMediaIdsSpecified || request.ImageMediaIds is null)
        {
            return;
        }

        // STEP-02: Prepare oldIds through OrderBy(filtered).Select(filtered).ToArray and newIds through request.ImageMediaIds.ToArray.
        var oldIds = restaurant.Images.OrderBy(image => image.SortOrder).Select(image => image.MediaId).ToArray();
        var newIds = request.ImageMediaIds.ToArray();
        // STEP-03: Stop here when oldIds.SequenceEqual(newIds) holds.
        if (oldIds.SequenceEqual(newIds))
        {
            return;
        }

        // STEP-04: Set oldValues["image_media_ids"] and newValues["image_media_ids"].
        oldValues["image_media_ids"] = oldIds;
        newValues["image_media_ids"] = newIds;
        // STEP-05: Prepare newIdSet through newIds.ToHashSet.
        var newIdSet = newIds.ToHashSet();
        // STEP-06: For each removedImage in restaurant.Images.Where(filtered).ToArray(), call restaurant.Images.Remove.
        foreach (var removedImage in restaurant.Images.Where(image => !newIdSet.Contains(image.MediaId)).ToArray())
        {
            restaurant.Images.Remove(removedImage);
        }

        // STEP-07: Prepare existingImages through restaurant.Images.ToDictionary.
        var existingImages = restaurant.Images.ToDictionary(image => image.MediaId);
        // STEP-08: While index < newIds.Length, check whether existingImages has an entry for newIds[index].
        for (short index = 0; index < newIds.Length; index++)
        {
            if (existingImages.TryGetValue(newIds[index], out var existingImage))
            {
                existingImage.SortOrder = index;
            }
            else
            {
                restaurant.Images.Add(new RestaurantImage { MediaId = newIds[index], SortOrder = index });
            }
        }
    }

    /// <summary>Updates one supplied value and appends its before/after values to the audit dictionaries.</summary>
    private static void Apply<T>(
        bool specified,
        string column,
        T currentValue,
        T newValue,
        Action<T> setter,
        IDictionary<string, object?> oldValues,
        IDictionary<string, object?> newValues)
    {
        // STEP-01: Stop here when specified is false or currentValue equals newValue.
        if (!specified || EqualityComparer<T>.Default.Equals(currentValue, newValue))
        {
            return;
        }

        // STEP-02: Set oldValues[column] and newValues[column].
        oldValues[column] = currentValue;
        newValues[column] = newValue;
        // STEP-03: Call setter.
        setter(newValue);
    }

    /// <summary>Trims optional text and converts blank values to null.</summary>
    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
