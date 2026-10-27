using System.Text.Json;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant;
using ANGI.Domain.Entities;

namespace ANGI.Application.UseCases.Discovery;

/// <summary>Maps a dish loaded by IDishRepository.GetPublicDetailAsync to DishDetail (DISH-02, DISH-03).</summary>
internal static class DishDetailMapper
{
    /// <param name="rank">Position in a reco list (DISH-03), or null when the dish was not ranked (DISH-02).</param>
    /// <param name="distanceKm">Distance to the caller's point, when the request sent one.</param>
    public static DishDetailDto Map(Dish dish, TimeProvider timeProvider, int? rank = null, decimal? distanceKm = null)
    {
        var restaurant = dish.Restaurant;
        return new DishDetailDto
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
                .OrderBy(dishTag => dishTag.Tag.Id)
                .Select(dishTag => new TagDto
                {
                    Id = dishTag.Tag.Id,
                    TagType = ToApiValue(dishTag.Tag.TagType),
                    Code = dishTag.Tag.Code,
                    Name = dishTag.Tag.Name
                })
                .ToArray(),
            Rank = rank,
            Description = dish.Description,
            Images = dish.Images
                .OrderBy(image => image.SortOrder)
                .Select(image => image.Media.Url)
                .ToArray(),
            Status = ToApiValue(dish.Status),
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
                IsOpenNow = OwnerRestaurantMapper.IsOpenNow(restaurant.BusinessHours, timeProvider),
                DistanceKm = distanceKm
            }
        };
    }

    private static string ToApiValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
