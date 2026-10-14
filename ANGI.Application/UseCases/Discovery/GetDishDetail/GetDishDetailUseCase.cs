using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant;
using ANGI.Domain.Entities;

namespace ANGI.Application.UseCases.Discovery.GetDishDetail;

/// <summary>Implements DISH-02: a public dish with the restaurant that serves it.</summary>
public sealed class GetDishDetailUseCase : IGetDishDetailUseCase
{
    private readonly IDishRepository _dishRepository;
    private readonly TimeProvider _timeProvider;

    public GetDishDetailUseCase(IDishRepository dishRepository, TimeProvider timeProvider)
    {
        _dishRepository = dishRepository;
        _timeProvider = timeProvider;
    }

    public async Task<DishDetailDto> ExecuteAsync(int dishId, CancellationToken ct)
    {
        var dish = await _dishRepository.GetPublicDetailAsync(dishId, ct)
            ?? throw new NotFoundException("DISH_NOT_FOUND", "Không tìm thấy món ăn.");

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
            Rank = null,
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
                District = restaurant.District,
                ProvinceName = restaurant.ProvinceName,
                Latitude = restaurant.Latitude,
                Longitude = restaurant.Longitude,
                PriceLevel = restaurant.PriceLevel,
                RatingAvg = restaurant.RatingAvg,
                RatingCount = restaurant.RatingCount,
                OperatingStatus = ToApiValue(restaurant.OperatingStatus),
                IsOpenNow = OwnerRestaurantMapper.IsOpenNow(restaurant.BusinessHours, _timeProvider),
                // DISH-02 takes no lat/lng, so there is no distance to report.
                DistanceKm = null
            }
        };
    }

    private static string ToApiValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
