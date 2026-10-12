using System.Globalization;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.DTOs.Account;
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
        var latestVerification = restaurant.Verifications
            .OrderByDescending(item => item.SubmittedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefault();
        var openMenuSubmission = restaurant.MenuSubmissions
            .Where(item => item.Status is MenuSubmissionStatus.Draft or MenuSubmissionStatus.Pending)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefault();

        return new OwnerRestaurantDto
        {
            Id = restaurant.Id,
            Name = restaurant.Name,
            Slug = restaurant.Slug,
            CoverUrl = GetMediaUrl(restaurant.CoverMediaId, restaurant.CoverMedia, mediaUrlOverrides),
            AddressLine = restaurant.AddressLine,
            District = restaurant.District,
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
                : MapVerification(latestVerification, cloudinaryService),
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

    /// <summary>Maps the latest verification and signs every private document URL.</summary>
    private static VerificationDto MapVerification(
        RestaurantVerification verification,
        ICloudinaryService cloudinaryService)
    {
        return new VerificationDto
        {
            Id = verification.Id,
            RestaurantId = verification.RestaurantId,
            Status = ToApiValue(verification.Status),
            LegalName = verification.LegalName ?? string.Empty,
            BusinessLicenseNo = verification.BusinessLicenseNo ?? string.Empty,
            TaxCode = verification.TaxCode,
            OwnerNote = verification.OwnerNote,
            Documents = verification.Documents.Select(document => new VerificationDocumentDto
            {
                DocType = ToApiValue(document.DocType),
                MediaId = document.MediaId,
                Url = cloudinaryService.GetUrl(document.Media.StorageKey)
            }).ToArray(),
            PreviousId = verification.PreviousId,
            SubmittedAt = verification.SubmittedAt,
            ReviewedBy = verification.Reviewer is null
                ? null
                : new UserSummaryDto
                {
                    Id = verification.Reviewer.Id,
                    DisplayName = verification.Reviewer.DisplayName,
                    AvatarUrl = verification.Reviewer.AvatarMedia?.Url
                },
            ReviewedAt = verification.ReviewedAt,
            ReviewNote = verification.ReviewNote
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
        if (!mediaId.HasValue)
        {
            return null;
        }

        if (mediaUrlOverrides is not null && mediaUrlOverrides.TryGetValue(mediaId.Value, out var url))
        {
            return url;
        }

        return media?.Id == mediaId.Value ? media.Url : null;
    }

    /// <summary>Determines whether any configured interval is open at the current Vietnam time.</summary>
    private static bool IsOpenNow(IEnumerable<RestaurantBusinessHour> hours, TimeProvider timeProvider)
    {
        var vietnamNow = timeProvider.GetUtcNow().ToOffset(TimeSpan.FromHours(7));
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
}
