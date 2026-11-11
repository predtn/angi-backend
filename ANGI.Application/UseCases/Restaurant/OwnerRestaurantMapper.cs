using System.Globalization;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Restaurant;

/// <summary>Maps restaurant aggregates to the complete owner-facing API contract.</summary>
internal static class OwnerRestaurantMapper
{
    /// <summary>Builds OwnerRestaurant including workflow summaries and signed verification documents.</summary>
    public static OwnerRestaurantDto Map(
        Domain.Entities.Restaurant restaurant,
        ICloudinaryService cloudinaryService,
        TimeProvider timeProvider,
        IReadOnlyDictionary<long, string>? mediaUrlOverrides = null)
    {
        // STEP-01: Prepare latestVerification through ThenByDescending(filtered).FirstOrDefault and openMenuSubmission through ThenByDescending(filtered).FirstOrDefault.
        var latestVerification = restaurant.Verifications
            .OrderByDescending(item => item.SubmittedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefault();
        var openMenuSubmission = restaurant.MenuSubmissions
            .Where(item => item.Status is MenuSubmissionStatus.Draft or MenuSubmissionStatus.Pending)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefault();

        // STEP-02: Return a new OwnerRestaurantDto.
        return new OwnerRestaurantDto
        {
            Id = restaurant.Id,
            Name = restaurant.Name,
            Slug = restaurant.Slug,
            CoverUrl = GetMediaUrl(restaurant.CoverMediaId, restaurant.CoverMedia, mediaUrlOverrides),
            AddressLine = restaurant.AddressLine,
            ProvinceName = restaurant.ProvinceName,
            Latitude = restaurant.Latitude,
            Longitude = restaurant.Longitude,
            PriceLevel = restaurant.PriceLevel,
            RatingAvg = restaurant.RatingAvg,
            RatingCount = restaurant.RatingCount,
            OperatingStatus = ToApiValue(restaurant.OperatingStatus),
            IsOpenNow = IsOpenNow(restaurant.BusinessHours, timeProvider),
            DistanceKm = null,
            Description = restaurant.Description,
            Phone = restaurant.Phone,
            Email = restaurant.Email,
            Website = restaurant.Website,
            Ward = restaurant.Ward,
            Images = restaurant.Images
                .OrderBy(image => image.SortOrder)
                .Select(image => GetMediaUrl(image.MediaId, image.Media, mediaUrlOverrides) ?? string.Empty)
                .ToArray(),
            ImageMediaIds = restaurant.Images
                .OrderBy(image => image.SortOrder)
                .Select(image => image.MediaId)
                .ToArray(),
            BusinessHours = restaurant.BusinessHours
                .OrderBy(hour => hour.DayOfWeek)
                .ThenBy(hour => hour.OpenTime)
                .Select(hour => new BusinessHourDto
                {
                    DayOfWeek = hour.DayOfWeek,
                    OpenTime = hour.OpenTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    CloseTime = hour.CloseTime.ToString("HH:mm", CultureInfo.InvariantCulture)
                })
                .ToArray(),
            Menu = null,
            MyReview = null,
            VerificationStatus = ToApiValue(restaurant.VerificationStatus),
            ModerationStatus = ToApiValue(restaurant.ModerationStatus),
            LatestVerification = latestVerification is null
                ? null
                : RestaurantVerificationMapper.Map(latestVerification, cloudinaryService),
            OpenMenuSubmission = openMenuSubmission is null
                ? null
                : new OpenMenuSubmissionDto
                {
                    Id = openMenuSubmission.Id,
                    Status = ToApiValue(openMenuSubmission.Status)
                },
            CreatedAt = restaurant.CreatedAt,
            UpdatedAt = restaurant.UpdatedAt
        };
    }

    /// <summary>Converts a domain enum to the snake_case value documented by the API.</summary>
    private static string ToApiValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>Uses freshly validated media URLs when a PATCH changes an unloaded navigation.</summary>
    private static string? GetMediaUrl(
        long? mediaId,
        MediaFile? media,
        IReadOnlyDictionary<long, string>? mediaUrlOverrides)
    {
        // STEP-01: Return null early when mediaId has no value.
        if (!mediaId.HasValue)
        {
            return null;
        }

        // STEP-02: Return url early when mediaUrlOverrides exists and mediaUrlOverrides has an entry for mediaId.Value.
        if (mediaUrlOverrides is not null && mediaUrlOverrides.TryGetValue(mediaId.Value, out var url))
        {
            return url;
        }

        // STEP-03: Return one of two results depending on media.Id is mediaId.Value.
        return media?.Id == mediaId.Value ? media.Url : null;
    }

    /// <summary>Determines whether any configured interval is open at the current Vietnam time.</summary>
    internal static bool IsOpenNow(IEnumerable<RestaurantBusinessHour> hours, TimeProvider timeProvider)
    {
        // STEP-01: Prepare vietnamNow through timeProvider.GetUtcNow().ToOffset, currentDay, previousDay and currentTime through TimeOnly.FromDateTime.
        var vietnamNow = timeProvider.GetUtcNow().ToOffset(TimeSpan.FromHours(7));
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
}
