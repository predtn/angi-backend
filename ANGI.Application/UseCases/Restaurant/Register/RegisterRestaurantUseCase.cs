using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Restaurant.Register;

/// <summary>Implements OWN-01 by creating a restaurant and its owned child records.</summary>
public sealed class RegisterRestaurantUseCase : IRegisterRestaurantUseCase
{
    private static readonly IReadOnlySet<string> _allowedImageMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };

    private readonly IValidator<RegisterRestaurantRequestDto> _validator;
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the restaurant registration workflow and its persistence dependencies.</summary>
    public RegisterRestaurantUseCase(
        IValidator<RegisterRestaurantRequestDto> validator,
        IRestaurantRepository restaurantRepository,
        IMediaRepository mediaRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _restaurantRepository = restaurantRepository;
        _mediaRepository = mediaRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Validates ownership and media references, then creates the restaurant aggregate.</summary>
    public async Task<OwnerRestaurantDto> ExecuteAsync(
        RegisterRestaurantRequestDto request,
        CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare ownerId.
        var ownerId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để đăng ký nhà hàng.");

        // STEP-03: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: Load restaurantRepository.LockOwnerForRegistrationAsync.
        await _restaurantRepository.LockOwnerForRegistrationAsync(ownerId, ct);

        // STEP-05: Reject with OWNER_ALREADY_HAS_RESTAURANT when _restaurantRepository.ExistsByOwnerIdAsync() is true.
        if (await _restaurantRepository.ExistsByOwnerIdAsync(ownerId, ct))
        {
            throw new ConflictException(
                "OWNER_ALREADY_HAS_RESTAURANT",
                "Chủ nhà hàng đã đăng ký một nhà hàng.");
        }

        // STEP-06: Prepare mediaIds through GetMediaIds and mediaFiles from mediaRepository.GetOwnedByIdsAsync.
        var mediaIds = GetMediaIds(request);
        var mediaFiles = mediaIds.Count == 0
            ? Array.Empty<MediaFile>()
            : await _mediaRepository.GetOwnedByIdsAsync(ownerId, mediaIds, ct);
        // STEP-07: Check the valid media rule through EnsureValidMedia.
        EnsureValidMedia(request, mediaFiles);

        // STEP-08: Prepare mediaById through mediaFiles.ToDictionary and restaurant as Domain.Entities.Restaurant.
        var mediaById = mediaFiles.ToDictionary(media => media.Id);
        var restaurant = new Domain.Entities.Restaurant
        {
            OwnerId = ownerId,
            Name = request.Name.Trim(),
            Slug = await CreateUniqueSlugAsync(request.Name, ct),
            Description = NormalizeOptional(request.Description),
            Phone = request.Phone.Trim(),
            Email = NormalizeOptional(request.Email)?.ToLowerInvariant(),
            Website = NormalizeOptional(request.Website),
            AddressLine = request.AddressLine.Trim(),
            Ward = NormalizeOptional(request.Ward),
            ProvinceName = request.ProvinceName.Trim(),
            Latitude = request.Latitude!.Value,
            Longitude = request.Longitude!.Value,
            PriceLevel = request.PriceLevel,
            CoverMediaId = request.CoverMediaId,
            VerificationStatus = RestaurantVerificationStatus.Unverified,
            OperatingStatus = RestaurantOperatingStatus.Open,
            ModerationStatus = RestaurantModerationStatus.Visible
        };

        // STEP-09: Queue restaurant through AddImages, queue restaurant through AddBusinessHours and queue restaurant through restaurantRepository.Add.
        AddImages(restaurant, request.ImageMediaIds);
        AddBusinessHours(restaurant, request.BusinessHours);

        _restaurantRepository.Add(restaurant);
        // STEP-10: Save the pending changes.
        await _unitOfWork.SaveChangesAsync(ct);
        // STEP-11: Commit the transaction.
        await transaction.CommitAsync(ct);

        // STEP-12: Return the result of MapToDto.
        return MapToDto(restaurant, request, mediaById);
    }

    /// <summary>Collects distinct cover and gallery media identifiers from the request.</summary>
    private static IReadOnlyCollection<long> GetMediaIds(RegisterRestaurantRequestDto request)
    {
        // STEP-01: Prepare ids as HashSet<long>.
        var ids = new HashSet<long>(request.ImageMediaIds ?? Array.Empty<long>());
        // STEP-02: When request.CoverMediaId has a value, call ids.Add.
        if (request.CoverMediaId.HasValue)
        {
            ids.Add(request.CoverMediaId.Value);
        }

        // STEP-03: Return ids.
        return ids;
    }

    /// <summary>
    /// Raises VALIDATION_FAILED on the field that holds the media id (coverMediaId or imageMediaIds)
    /// when the media is absent, belongs to another user, or is not a supported restaurant image.
    /// </summary>
    private static void EnsureValidMedia(
        RegisterRestaurantRequestDto request,
        IReadOnlyCollection<MediaFile> mediaFiles)
    {
        // STEP-01: Prepare foundIds through Where(filtered).Select(filtered).ToHashSet and failures as List<ValidationFailure>.
        var foundIds = mediaFiles
            .Where(media => !string.IsNullOrWhiteSpace(media.MimeType) && _allowedImageMimeTypes.Contains(media.MimeType.Trim()))
            .Select(media => media.Id).ToHashSet();
        var failures = new List<ValidationFailure>();

        // STEP-02: When request.CoverMediaId exists and foundIds does not contain coverMediaId, call failures.Add.
        if (request.CoverMediaId is { } coverMediaId && !foundIds.Contains(coverMediaId))
        {
            failures.Add(new ValidationFailure(
                nameof(request.CoverMediaId),
                $"Ảnh bìa {coverMediaId} không tồn tại, không thuộc tài khoản của bạn hoặc không phải ảnh hợp lệ."));
        }

        // STEP-03: Prepare missingImageIds through Empty<long>().Where(filtered).ToArray.
        var missingImageIds = (request.ImageMediaIds ?? Array.Empty<long>())
            .Where(id => !foundIds.Contains(id))
            .ToArray();
        // STEP-04: When missingImageIds.Length > 0, call failures.Add.
        if (missingImageIds.Length > 0)
        {
            failures.Add(new ValidationFailure(
                nameof(request.ImageMediaIds),
                $"Ảnh {string.Join(", ", missingImageIds)} không tồn tại, không thuộc tài khoản của bạn hoặc không phải ảnh hợp lệ."));
        }

        // STEP-05: Throw ValidationException when failures.Count > 0.
        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }

    /// <summary>Creates a URL-safe, unique slug while preserving the readable restaurant name.</summary>
    private async Task<string> CreateUniqueSlugAsync(string name, CancellationToken ct)
    {
        // STEP-01: Prepare normalized through name.Trim().ToLowerInvariant().Replace, decomposed through normalized.Normalize and builder as StringBuilder.
        var normalized = name.Trim().ToLowerInvariant().Replace('đ', 'd');
        var decomposed = normalized.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        // STEP-02: For each character in decomposed, check whether CharUnicodeInfo.GetUnicodeCategory() is not UnicodeCategory.NonSpacingMark.
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        // STEP-03: Prepare baseSlug through Regex.Replace().Trim.
        var baseSlug = Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), "[^a-z0-9]+", "-")
            .Trim('-');
        // STEP-04: When baseSlug.Length is 0, set baseSlug.
        if (baseSlug.Length == 0)
        {
            baseSlug = "restaurant";
        }

        // STEP-05: Prepare slug and suffix.
        var slug = baseSlug;
        var suffix = 2;
        // STEP-06: While _restaurantRepository.SlugExistsAsync() is true, set slug.
        while (await _restaurantRepository.SlugExistsAsync(slug, ct))
        {
            slug = $"{baseSlug}-{suffix++}";
        }

        // STEP-07: Return slug.
        return slug;
    }

    /// <summary>Adds gallery image join rows in the same order supplied by the owner.</summary>
    private static void AddImages(
        Domain.Entities.Restaurant restaurant,
        IReadOnlyList<long>? imageMediaIds)
    {
        if (imageMediaIds is null)
        {
            return;
        }

        for (short index = 0; index < imageMediaIds.Count; index++)
        {
            restaurant.Images.Add(new RestaurantImage
            {
                MediaId = imageMediaIds[index],
                SortOrder = index
            });
        }
    }

    /// <summary>Parses validated HH:mm values into restaurant business-hour entities.</summary>
    private static void AddBusinessHours(
        Domain.Entities.Restaurant restaurant,
        IReadOnlyList<BusinessHourDto>? businessHours)
    {
        if (businessHours is null)
        {
            return;
        }

        foreach (var hour in businessHours)
        {
            restaurant.BusinessHours.Add(new RestaurantBusinessHour
            {
                DayOfWeek = hour.DayOfWeek,
                OpenTime = TimeOnly.ParseExact(hour.OpenTime, "HH:mm", CultureInfo.InvariantCulture),
                CloseTime = TimeOnly.ParseExact(hour.CloseTime, "HH:mm", CultureInfo.InvariantCulture)
            });
        }
    }

    /// <summary>Maps the newly persisted restaurant aggregate to the documented owner response.</summary>
    private OwnerRestaurantDto MapToDto(
        Domain.Entities.Restaurant restaurant,
        RegisterRestaurantRequestDto request,
        IReadOnlyDictionary<long, MediaFile> mediaById)
    {
        var hours = restaurant.BusinessHours
            .OrderBy(hour => hour.DayOfWeek)
            .ThenBy(hour => hour.OpenTime)
            .Select(hour => new BusinessHourDto
            {
                DayOfWeek = hour.DayOfWeek,
                OpenTime = hour.OpenTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                CloseTime = hour.CloseTime.ToString("HH:mm", CultureInfo.InvariantCulture)
            })
            .ToArray();

        return new OwnerRestaurantDto
        {
            Id = restaurant.Id,
            Name = restaurant.Name,
            Slug = restaurant.Slug,
            CoverUrl = restaurant.CoverMediaId.HasValue
                ? mediaById[restaurant.CoverMediaId.Value].Url
                : null,
            AddressLine = restaurant.AddressLine,
            ProvinceName = restaurant.ProvinceName,
            Latitude = restaurant.Latitude,
            Longitude = restaurant.Longitude,
            PriceLevel = restaurant.PriceLevel,
            RatingAvg = restaurant.RatingAvg,
            RatingCount = restaurant.RatingCount,
            OperatingStatus = ToApiValue(restaurant.OperatingStatus),
            IsOpenNow = IsOpenNow(restaurant.BusinessHours),
            DistanceKm = null,
            Description = restaurant.Description,
            Phone = restaurant.Phone,
            Email = restaurant.Email,
            Website = restaurant.Website,
            Ward = restaurant.Ward,
            Images = (request.ImageMediaIds ?? Array.Empty<long>())
                .Select(id => mediaById[id].Url)
                .ToArray(),
            ImageMediaIds = (request.ImageMediaIds ?? Array.Empty<long>()).ToArray(),
            BusinessHours = hours,
            Menu = null,
            MyReview = null,
            VerificationStatus = ToApiValue(restaurant.VerificationStatus),
            ModerationStatus = ToApiValue(restaurant.ModerationStatus),
            LatestVerification = null,
            OpenMenuSubmission = null,
            CreatedAt = restaurant.CreatedAt,
            UpdatedAt = restaurant.UpdatedAt
        };
    }

    /// <summary>Converts an enum to its sheet "Enum" value (TemporarilyClosed → temporarily_closed).</summary>
    private static string ToApiValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>Determines whether any configured interval is open at the current Vietnam time.</summary>
    private bool IsOpenNow(IEnumerable<RestaurantBusinessHour> hours)
    {
        // STEP-01: Prepare vietnamNow through timeProvider.GetUtcNow().ToOffset, currentDay, previousDay and currentTime through TimeOnly.FromDateTime.
        var vietnamNow = _timeProvider.GetUtcNow().ToOffset(TimeSpan.FromHours(7));
        var currentDay = (short)vietnamNow.DayOfWeek;
        var previousDay = (short)((currentDay + 6) % 7);
        var currentTime = TimeOnly.FromDateTime(vietnamNow.DateTime);

        // STEP-02: Return the computed condition.
        return hours.Any(hour =>
            hour.DayOfWeek == currentDay &&
            (hour.CloseTime > hour.OpenTime
                ? currentTime >= hour.OpenTime && currentTime < hour.CloseTime
                : currentTime >= hour.OpenTime)) ||
            hours.Any(hour =>
                hour.DayOfWeek == previousDay &&
                hour.CloseTime < hour.OpenTime &&
                currentTime < hour.CloseTime);
    }

    /// <summary>Trims optional text and converts blank values to null.</summary>
    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
