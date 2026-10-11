using System.Text.Json;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;

namespace ANGI.Application.UseCases.Restaurant;

/// <summary>Maps restaurant menu aggregates to owner-facing API contracts.</summary>
internal static class RestaurantMenuMapper
{
    /// <summary>Maps the active menu and optional open workflow summary.</summary>
    public static OwnerMenuDto MapOwnerMenu(Domain.Entities.Restaurant restaurant)
    {
        var openSubmission = restaurant.MenuSubmissions
            .OrderByDescending(submission => submission.CreatedAt)
            .ThenByDescending(submission => submission.Id)
            .FirstOrDefault();

        return new OwnerMenuDto
        {
            Dishes = restaurant.Dishes
                .OrderBy(dish => dish.SortOrder)
                .ThenBy(dish => dish.Id)
                .Select(MapOwnerDish)
                .ToArray(),
            OpenSubmission = openSubmission is null
                ? null
                : new OpenMenuSubmissionDto
                {
                    Id = openSubmission.Id,
                    Status = ToApiValue(openSubmission.Status)
                }
        };
    }

    /// <summary>Maps one menu submission without its item collection.</summary>
    public static MenuSubmissionDto MapSubmission(MenuSubmission submission)
    {
        return new MenuSubmissionDto
        {
            Id = submission.Id,
            RestaurantId = submission.RestaurantId,
            Status = ToApiValue(submission.Status),
            OwnerNote = submission.OwnerNote,
            SubmittedAt = submission.SubmittedAt,
            ReviewedBy = submission.Reviewer is null
                ? null
                : new UserSummaryDto
                {
                    Id = submission.Reviewer.Id,
                    DisplayName = submission.Reviewer.DisplayName,
                    AvatarUrl = submission.Reviewer.AvatarMedia?.Url
                },
            ReviewedAt = submission.ReviewedAt,
            ReviewNote = submission.ReviewNote,
            CreatedAt = submission.CreatedAt
        };
    }

    /// <summary>Maps a submission and all proposed menu items.</summary>
    public static MenuSubmissionWithItemsDto MapSubmissionWithItems(MenuSubmission submission)
    {
        var source = MapSubmission(submission);
        return new MenuSubmissionWithItemsDto
        {
            Id = source.Id,
            RestaurantId = source.RestaurantId,
            Status = source.Status,
            OwnerNote = source.OwnerNote,
            SubmittedAt = source.SubmittedAt,
            ReviewedBy = source.ReviewedBy,
            ReviewedAt = source.ReviewedAt,
            ReviewNote = source.ReviewNote,
            CreatedAt = source.CreatedAt,
            Items = submission.Items
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .Select(MapItem)
                .ToArray()
        };
    }

    /// <summary>Maps a freshly cloned draft without attaching existing dishes to the new EF graph.</summary>
    public static MenuSubmissionWithItemsDto MapClonedSubmission(
        MenuSubmission submission,
        IReadOnlyCollection<Dish> sourceDishes)
    {
        var source = MapSubmission(submission);
        var dishesById = sourceDishes.ToDictionary(dish => dish.Id);
        return new MenuSubmissionWithItemsDto
        {
            Id = source.Id,
            RestaurantId = source.RestaurantId,
            Status = source.Status,
            OwnerNote = source.OwnerNote,
            SubmittedAt = source.SubmittedAt,
            ReviewedBy = source.ReviewedBy,
            ReviewedAt = source.ReviewedAt,
            ReviewNote = source.ReviewNote,
            CreatedAt = source.CreatedAt,
            Items = submission.Items
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .Select(item =>
                {
                    var dish = dishesById[item.SourceDishId!.Value];
                    return MapItem(item, "unchanged", dish.CoverMedia?.Url);
                })
                .ToArray()
        };
    }

    /// <summary>Maps a submission and computes dishes omitted from its proposed item set.</summary>
    public static MenuSubmissionDetailDto MapSubmissionDetail(MenuSubmission submission)
    {
        var source = MapSubmissionWithItems(submission);
        var retainedDishIds = submission.Items
            .Where(item => item.SourceDishId.HasValue)
            .Select(item => item.SourceDishId!.Value)
            .ToHashSet();

        return new MenuSubmissionDetailDto
        {
            Id = source.Id,
            RestaurantId = source.RestaurantId,
            Status = source.Status,
            OwnerNote = source.OwnerNote,
            SubmittedAt = source.SubmittedAt,
            ReviewedBy = source.ReviewedBy,
            ReviewedAt = source.ReviewedAt,
            ReviewNote = source.ReviewNote,
            CreatedAt = source.CreatedAt,
            Items = source.Items,
            RemovedDishes = submission.Restaurant.Dishes
                .Where(dish => !retainedDishIds.Contains(dish.Id))
                .OrderBy(dish => dish.SortOrder)
                .ThenBy(dish => dish.Id)
                .Select(MapDish)
                .ToArray()
        };
    }

    /// <summary>Maps one proposed item and derives its change classification.</summary>
    public static MenuSubmissionItemDto MapItem(MenuSubmissionItem item) =>
        MapItem(item, GetChange(item), item.CoverMedia?.Url);

    /// <summary>Maps an item while computing its change and supplying a detached cover URL.</summary>
    public static MenuSubmissionItemDto MapItem(MenuSubmissionItem item, string? coverUrl) =>
        MapItem(item, GetChange(item), coverUrl);

    /// <summary>Maps an item with response-only values that are intentionally not attached to EF.</summary>
    public static MenuSubmissionItemDto MapItem(
        MenuSubmissionItem item,
        string change,
        string? coverUrl)
    {
        return new MenuSubmissionItemDto
        {
            Id = item.Id,
            SourceDishId = item.SourceDishId,
            Change = change,
            Name = item.Name,
            Description = item.Description,
            DishForm = ToApiValue(item.DishForm),
            Price = item.Price,
            CoverMediaId = item.CoverMediaId,
            CoverUrl = coverUrl,
            TagIds = item.TagIds,
            SortOrder = item.SortOrder,
            ResultDishId = item.ResultDishId
        };
    }

    /// <summary>Maps a dish card and appends owner-only status and sort order.</summary>
    private static OwnerMenuDishDto MapOwnerDish(Dish dish)
    {
        var source = MapDish(dish);
        return new OwnerMenuDishDto
        {
            Id = source.Id,
            Name = source.Name,
            Price = source.Price,
            CoverUrl = source.CoverUrl,
            DishForm = source.DishForm,
            IsAvailable = source.IsAvailable,
            LikeCount = source.LikeCount,
            DislikeCount = source.DislikeCount,
            Tags = source.Tags,
            Restaurant = source.Restaurant,
            Rank = source.Rank,
            Status = ToApiValue(dish.Status),
            SortOrder = dish.SortOrder
        };
    }

    /// <summary>Maps a domain dish to the shared DishCard contract.</summary>
    private static DishCardDto MapDish(Dish dish)
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
                .OrderBy(dishTag => dishTag.TagId)
                .Select(dishTag => new TagDto
                {
                    Id = dishTag.Tag.Id,
                    TagType = ToApiValue(dishTag.Tag.TagType),
                    Code = dishTag.Tag.Code,
                    Name = dishTag.Tag.Name
                })
                .ToArray(),
            Restaurant = new RestaurantMiniDto
            {
                Id = dish.Restaurant.Id,
                Name = dish.Restaurant.Name,
                District = dish.Restaurant.District,
                ProvinceName = dish.Restaurant.ProvinceName,
                Latitude = dish.Restaurant.Latitude,
                Longitude = dish.Restaurant.Longitude,
                RatingAvg = dish.Restaurant.RatingAvg
            },
            Rank = null
        };
    }

    /// <summary>Compares a proposed item with its current source dish.</summary>
    private static string GetChange(MenuSubmissionItem item)
    {
        if (item.SourceDish is null)
        {
            return "new";
        }

        var sourceTagIds = item.SourceDish.DishTags.Select(tag => tag.TagId).OrderBy(id => id);
        var itemTagIds = item.TagIds.OrderBy(id => id);
        var unchanged = item.Name == item.SourceDish.Name &&
                        item.Description == item.SourceDish.Description &&
                        item.DishForm == item.SourceDish.DishForm &&
                        item.Price == item.SourceDish.Price &&
                        item.CoverMediaId == item.SourceDish.CoverMediaId &&
                        item.SortOrder == item.SourceDish.SortOrder &&
                        sourceTagIds.SequenceEqual(itemTagIds);
        return unchanged ? "unchanged" : "edited";
    }

    /// <summary>Converts a domain enum to its documented snake_case API value.</summary>
    private static string ToApiValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
