using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant;
using ANGI.Domain.Entities;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Maps verification aggregates to the MOD-V1 and MOD-V2 response contracts.</summary>
internal static class ModerationVerificationMapper
{
    /// <summary>Maps a queue aggregate to the flattened MOD-V1 card contract.</summary>
    public static ModVerificationCardDto MapCard(
        RestaurantVerification verification,
        ICloudinaryService cloudinaryService,
        TimeProvider timeProvider)
    {
        var source = RestaurantVerificationMapper.Map(verification, cloudinaryService);
        var restaurant = verification.Restaurant;
        var result = new ModVerificationCardDto
        {
            Restaurant = new RestaurantCardDto
            {
                Id = restaurant.Id,
                Name = restaurant.Name,
                Slug = restaurant.Slug,
                CoverUrl = restaurant.CoverMedia?.Url,
                AddressLine = restaurant.AddressLine,
                Ward = restaurant.Ward,
                ProvinceName = restaurant.ProvinceName,
                Latitude = restaurant.Latitude,
                Longitude = restaurant.Longitude,
                PriceLevel = restaurant.PriceLevel,
                RatingAvg = restaurant.RatingAvg,
                RatingCount = restaurant.RatingCount,
                OperatingStatus = ToApiValue(restaurant.OperatingStatus),
                IsOpenNow = restaurant.OperatingStatus == Domain.Enums.RestaurantOperatingStatus.Open
                    && OwnerRestaurantMapper.IsOpenNow(restaurant.BusinessHours, timeProvider),
                DistanceKm = null
            }
        };
        Copy(source, result);
        return result;
    }

    /// <summary>Maps a complete MOD-V2 aggregate and flattens the verification fields at the response root.</summary>
    public static VerificationReviewDetailDto MapDetail(
        RestaurantVerification verification,
        ICloudinaryService cloudinaryService,
        TimeProvider timeProvider)
    {
        var source = RestaurantVerificationMapper.Map(verification, cloudinaryService);
        var result = new VerificationReviewDetailDto
        {
            Restaurant = OwnerRestaurantMapper.Map(verification.Restaurant, cloudinaryService, timeProvider),
            Previous = verification.Previous is null
                ? null
                : RestaurantVerificationMapper.Map(verification.Previous, cloudinaryService)
        };
        Copy(source, result);
        return result;
    }

    /// <summary>Copies the shared Verification fields into a flattened moderation response.</summary>
    private static void Copy(VerificationDto source, VerificationDto destination)
    {
        destination.Id = source.Id;
        destination.RestaurantId = source.RestaurantId;
        destination.Status = source.Status;
        destination.LegalName = source.LegalName;
        destination.BusinessLicenseNo = source.BusinessLicenseNo;
        destination.TaxCode = source.TaxCode;
        destination.OwnerNote = source.OwnerNote;
        destination.Documents = source.Documents;
        destination.PreviousId = source.PreviousId;
        destination.SubmittedAt = source.SubmittedAt;
        destination.ReviewedBy = source.ReviewedBy;
        destination.ReviewedAt = source.ReviewedAt;
        destination.ReviewNote = source.ReviewNote;
    }

    /// <summary>Converts an enum name to its API snake_case value.</summary>
    private static string ToApiValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
