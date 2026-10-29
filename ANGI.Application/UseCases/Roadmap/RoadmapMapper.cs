using System.Text.Json;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Roadmap;

/// <summary>Maps roadmap aggregates to the complete public roadmap contract.</summary>
internal static class RoadmapMapper
{
    /// <summary>Builds Overview, Itinerary, and Budget from current dish prices.</summary>
    public static RoadmapDetailDto Map(
        Domain.Entities.Roadmap roadmap,
        IReadOnlyDictionary<long, FeedbackValue>? feedbackByItemId = null)
    {
        feedbackByItemId ??= new Dictionary<long, FeedbackValue>();
        var orderedDays = roadmap.Days.OrderBy(day => day.DayNumber).ToArray();
        var allItems = orderedDays
            .SelectMany(day => day.Items)
            .OrderBy(item => item.RoadmapDay.DayNumber)
            .ThenBy(item => item.MealSlot)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToArray();
        var total = allItems.Sum(item => item.Dish.Price);

        return new RoadmapDetailDto
        {
            Overview = new RoadmapSummaryDto
            {
                Id = roadmap.Id,
                Version = roadmap.Version,
                Title = roadmap.Title,
                StartDate = roadmap.StartDate,
                EndDate = roadmap.StartDate?.AddDays(roadmap.NumDays - 1),
                NumDays = roadmap.NumDays,
                BudgetAmount = roadmap.BudgetAmount,
                TotalEstimatedCost = total,
                ItemCount = allItems.Length,
                CoverUrl = allItems.FirstOrDefault()?.Dish.CoverMedia?.Url,
                OriginMode = ToApiValue(roadmap.OriginMode),
                OriginLatitude = roadmap.OriginLatitude,
                OriginLongitude = roadmap.OriginLongitude,
                OriginLabel = roadmap.OriginLabel,
                CreatedAt = roadmap.CreatedAt,
                UpdatedAt = roadmap.UpdatedAt
            },
            Itinerary = orderedDays.Select(day => MapDay(roadmap, day, feedbackByItemId)).ToArray(),
            Budget = new RoadmapBudgetDto
            {
                BudgetAmount = roadmap.BudgetAmount,
                TotalEstimatedCost = total,
                Remaining = roadmap.BudgetAmount.HasValue ? roadmap.BudgetAmount.Value - total : null,
                IsOverBudget = roadmap.BudgetAmount.HasValue && total > roadmap.BudgetAmount.Value,
                ByDay = orderedDays.Select(day => new RoadmapDayCostDto
                {
                    DayNumber = day.DayNumber,
                    Cost = day.Items.Sum(item => item.Dish.Price)
                }).ToArray(),
                ByMealSlot = allItems
                    .GroupBy(item => item.MealSlot)
                    .OrderBy(group => group.Key)
                    .Select(group => new RoadmapMealSlotCostDto
                    {
                        MealSlot = ToApiValue(group.Key),
                        Cost = group.Sum(item => item.Dish.Price)
                    }).ToArray()
            }
        };
    }

    /// <summary>Maps one roadmap day and preserves the API-defined item ordering.</summary>
    private static RoadmapDayDto MapDay(
        Domain.Entities.Roadmap roadmap,
        RoadmapDay day,
        IReadOnlyDictionary<long, FeedbackValue> feedbackByItemId)
    {
        var items = day.Items
            .OrderBy(item => item.MealSlot)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => MapItem(day, item, feedbackByItemId))
            .ToArray();

        return new RoadmapDayDto
        {
            DayId = day.Id,
            DayNumber = day.DayNumber,
            Date = roadmap.StartDate?.AddDays(day.DayNumber - 1),
            Note = day.Note,
            DayCost = items.Sum(item => item.Dish.Price),
            Items = items
        };
    }

    /// <summary>Maps one scheduled item, its dish card, and the current traveler's feedback.</summary>
    internal static RoadmapItemDto MapItem(
        RoadmapDay day,
        RoadmapItem item,
        IReadOnlyDictionary<long, FeedbackValue>? feedbackByItemId = null)
    {
        var dish = item.Dish;
        var restaurant = dish.Restaurant;
        return new RoadmapItemDto
        {
            Id = item.Id,
            DayId = day.Id,
            DayNumber = day.DayNumber,
            MealSlot = ToApiValue(item.MealSlot),
            SortOrder = item.SortOrder,
            Source = ToApiValue(item.Source),
            RecoRequestId = item.RecoRequestId,
            Dish = new RoadmapDishDto
            {
                Id = dish.Id,
                Name = dish.Name,
                Price = dish.Price,
                CoverUrl = dish.CoverMedia?.Url,
                DishForm = ToApiValue(dish.DishForm),
                IsAvailable = dish.IsAvailable,
                LikeCount = dish.LikeCount,
                DislikeCount = dish.DislikeCount,
                Tags = dish.DishTags.Select(dishTag => new TagDto
                {
                    Id = dishTag.Tag.Id,
                    TagType = ToApiValue(dishTag.Tag.TagType),
                    Code = dishTag.Tag.Code,
                    Name = dishTag.Tag.Name
                }).ToArray(),
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
                Rank = null,
                Status = ToApiValue(dish.Status)
            },
            MyFeedback = feedbackByItemId is not null && feedbackByItemId.TryGetValue(item.Id, out var feedback)
                ? ToApiValue(feedback)
                : null,
            CreatedAt = item.CreatedAt
        };
    }

    /// <summary>Converts domain enum names to the snake-case values defined by the API.</summary>
    private static string ToApiValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
