using System.Globalization;
using System.Text.Json;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Restaurant;

/// <summary>Maps restaurant entities and navigations to public API DTOs.</summary>
internal static class RestaurantDetailMapper
{
    /// <summary>Maps a restaurant entity to RestaurantDetailDto for REST-02.</summary>
    public static RestaurantDetailDto MapDetail(
        Domain.Entities.Restaurant restaurant,
        TimeProvider timeProvider,
        decimal? distanceKm = null,
        ReviewDto? myReview = null,
        IReadOnlyList<DishCardDto>? menu = null)
    {
        return new RestaurantDetailDto
        {
            Id = restaurant.Id,
            Name = restaurant.Name,
            Slug = restaurant.Slug,
            CoverUrl = restaurant.CoverMedia?.Url,
            AddressLine = restaurant.AddressLine,
            ProvinceName = restaurant.ProvinceName,
            Latitude = restaurant.Latitude,
            Longitude = restaurant.Longitude,
            PriceLevel = restaurant.PriceLevel,
            RatingAvg = restaurant.RatingAvg,
            RatingCount = restaurant.RatingCount,
            OperatingStatus = ToApiValue(restaurant.OperatingStatus),
            IsOpenNow = IsOpenNow(restaurant.BusinessHours, timeProvider),
            DistanceKm = distanceKm,
            Description = restaurant.Description,
            Phone = restaurant.Phone,
            Email = restaurant.Email,
            Website = restaurant.Website,
            Ward = restaurant.Ward,
            Images = restaurant.Images
                .OrderBy(image => image.SortOrder)
                .Select(image => image.Media.Url)
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
            Menu = menu,
            MyReview = myReview
        };
    }

    /// <summary>Maps a dish entity and its owning restaurant to DishCardDto for REST-03.</summary>
    public static DishCardDto MapDishCard(Dish dish, Domain.Entities.Restaurant restaurant)
    {
        return new DishCardDto
        {
            Id = dish.Id,
            Name = dish.Name,
            Price = dish.Price,
            CoverUrl = dish.CoverMedia?.Url,
            DishForm = ToApiValue(dish.DishForm),
            IsAvailable = dish.IsAvailable,
            LikeCount = dish.LikeCount,
            DislikeCount = dish.DislikeCount,
            Tags = dish.DishTags
                .Select(dt => new TagDto
                {
                    Id = dt.Tag.Id,
                    TagType = ToApiValue(dt.Tag.TagType),
                    Code = dt.Tag.Code,
                    Name = dt.Tag.Name
                })
                .ToArray(),
            Restaurant = new RestaurantMiniDto
            {
                Id = restaurant.Id,
                Name = restaurant.Name,
                Ward = restaurant.Ward,
                ProvinceName = restaurant.ProvinceName,
                Latitude = restaurant.Latitude,
                Longitude = restaurant.Longitude,
                RatingAvg = restaurant.RatingAvg
            },
            Rank = null
        };
    }

    /// <summary>Maps a RestaurantReview entity to ReviewDto for REST-04.</summary>
    public static ReviewDto MapReview(RestaurantReview review) => MapReview(review, false);

    /// <summary>Maps a RestaurantReview entity to ReviewDto for REST-04 and OWN-19.</summary>
    public static ReviewDto MapReview(RestaurantReview review, bool includeAllReplies)
    {
        return new ReviewDto
        {
            Id = review.Id,
            RestaurantId = review.RestaurantId,
            User = new UserSummaryDto
            {
                Id = review.User.Id,
                DisplayName = review.User.DisplayName,
                AvatarUrl = review.User.AvatarMedia?.Url
            },
            Rating = review.Rating,
            Content = review.Content,
            Images = review.Images
                .Select(i => i.Media.Url)
                .ToArray(),
            RoadmapItemId = review.RoadmapItemId,
            Status = ToApiValue(review.Status),
            CreatedAt = review.CreatedAt,
            Reply = review.Reply != null && (includeAllReplies || review.Reply.Status == ContentStatus.Visible)
                ? MapReviewReply(review.Reply)
                : null
        };
    }

    /// <summary>Maps a ReviewReply entity to ReviewReplyDto for OWN-20.</summary>
    public static ReviewReplyDto MapReviewReply(ReviewReply reply)
    {
        return new ReviewReplyDto
        {
            Content = reply.Content,
            Status = ToApiValue(reply.Status),
            CreatedAt = reply.CreatedAt,
            UpdatedAt = reply.UpdatedAt
        };
    }

    /// <summary>Calculates the straight-line distance in kilometres between two geographic coordinates.</summary>
    public static decimal CalculateHaversineDistance(decimal lat1, decimal lng1, decimal lat2, decimal lng2)
    {
        const double EarthRadiusKm = 6371.0;
        var dLat = (double)(lat2 - lat1) * Math.PI / 180.0;
        var dLng = (double)(lng2 - lng1) * Math.PI / 180.0;
        var radLat1 = (double)lat1 * Math.PI / 180.0;
        var radLat2 = (double)lat2 * Math.PI / 180.0;

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(radLat1) * Math.Cos(radLat2) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return Math.Round((decimal)(EarthRadiusKm * c), 1);
    }

    /// <summary>Determines whether any restaurant interval is open at the current Vietnam local time.</summary>
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

    private static string ToApiValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
