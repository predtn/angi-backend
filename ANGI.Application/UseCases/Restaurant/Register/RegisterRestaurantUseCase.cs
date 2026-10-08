using System.Globalization;
using System.Text;
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
        await _validator.ValidateAndThrowAsync(request, ct);

        var ownerId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để đăng ký nhà hàng.");

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        await _restaurantRepository.LockOwnerForRegistrationAsync(ownerId, ct);

        if (await _restaurantRepository.ExistsByOwnerIdAsync(ownerId, ct))
        {
            throw new ConflictException(
                "OWNER_ALREADY_HAS_RESTAURANT",
                "Chủ nhà hàng đã đăng ký một nhà hàng.");
        }

        var mediaIds = GetMediaIds(request);
        var mediaFiles = mediaIds.Count == 0
            ? Array.Empty<MediaFile>()
            : await _mediaRepository.GetOwnedByIdsAsync(ownerId, mediaIds, ct);
        EnsureAllMediaExist(mediaIds, mediaFiles);

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
            District = request.District.Trim(),
            ProvinceName = request.ProvinceName.Trim(),
            Latitude = request.Latitude!.Value,
            Longitude = request.Longitude!.Value,
            PriceLevel = request.PriceLevel,
            CoverMediaId = request.CoverMediaId,
            VerificationStatus = RestaurantVerificationStatus.Unverified,
            OperatingStatus = RestaurantOperatingStatus.Open,
            ModerationStatus = RestaurantModerationStatus.Visible
        };

        AddImages(restaurant, request.ImageMediaIds);
        AddBusinessHours(restaurant, request.BusinessHours);

        _restaurantRepository.Add(restaurant);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return MapToDto(restaurant, request, mediaById);
    }

    /// <summary>Collects distinct cover and gallery media identifiers from the request.</summary>
    private static IReadOnlyCollection<long> GetMediaIds(RegisterRestaurantRequestDto request)
    {
        var ids = new HashSet<long>(request.ImageMediaIds ?? Array.Empty<long>());
        if (request.CoverMediaId.HasValue)
        {
            ids.Add(request.CoverMediaId.Value);
        }

        return ids;
    }

    /// <summary>Raises a documented validation error when media is absent or belongs to another user.</summary>
    private static void EnsureAllMediaExist(
        IReadOnlyCollection<long> requestedIds,
        IReadOnlyCollection<MediaFile> mediaFiles)
    {
        var foundIds = mediaFiles.Select(media => media.Id).ToHashSet();
        var missingIds = requestedIds.Where(id => !foundIds.Contains(id)).ToArray();
        if (missingIds.Length == 0)
        {
            return;
        }

        throw new ValidationException(
            new[]
            {
                new ValidationFailure(
                    "imageMediaIds",
                    $"Media không tồn tại hoặc không thuộc người dùng: {string.Join(", ", missingIds)}.")
            });
    }

    /// <summary>Creates a URL-safe, unique slug while preserving the readable restaurant name.</summary>
    private async Task<string> CreateUniqueSlugAsync(string name, CancellationToken ct)
    {
        var normalized = name.Trim().ToLowerInvariant().Replace('đ', 'd');
        var decomposed = normalized.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        var baseSlug = Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), "[^a-z0-9]+", "-")
            .Trim('-');
        if (baseSlug.Length == 0)
        {
            baseSlug = "restaurant";
        }

        var slug = baseSlug;
        var suffix = 2;
        while (await _restaurantRepository.SlugExistsAsync(slug, ct))
        {
            slug = $"{baseSlug}-{suffix++}";
        }

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
            District = restaurant.District,
            ProvinceName = restaurant.ProvinceName,
            Latitude = restaurant.Latitude,
            Longitude = restaurant.Longitude,
            PriceLevel = restaurant.PriceLevel,
            RatingAvg = restaurant.RatingAvg,
            RatingCount = restaurant.RatingCount,
            OperatingStatus = "open",
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
            BusinessHours = hours,
            Menu = null,
            MyReview = null,
            VerificationStatus = "unverified",
            ModerationStatus = "visible",
            LatestVerification = null,
            OpenMenuSubmission = null,
            CreatedAt = restaurant.CreatedAt
        };
    }

    /// <summary>Determines whether any configured interval is open at the current Vietnam time.</summary>
    private bool IsOpenNow(IEnumerable<RestaurantBusinessHour> hours)
    {
        var vietnamNow = _timeProvider.GetUtcNow().ToOffset(TimeSpan.FromHours(7));
        var currentDay = (short)vietnamNow.DayOfWeek;
        var previousDay = (short)((currentDay + 6) % 7);
        var currentTime = TimeOnly.FromDateTime(vietnamNow.DateTime);

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
