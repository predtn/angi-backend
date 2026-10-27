using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Restaurant;
using ANGI.Domain.Entities;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Maps menu moderation responses using the shared owner menu comparison rules.</summary>
internal static class MenuReviewMapper
{
    /// <summary>Maps one queue card with new, edited and removed counts.</summary>
    public static ModMenuSubmissionCardDto Card(MenuSubmission submission)
    {
        var source = RestaurantMenuMapper.MapSubmission(submission);
        var changes = submission.Items.Select(item => RestaurantMenuMapper.MapItem(item).Change).ToArray();
        var retained = submission.Items.Where(item => item.SourceDishId.HasValue)
            .Select(item => item.SourceDishId!.Value).ToHashSet();
        return new ModMenuSubmissionCardDto
        {
            Id = source.Id, RestaurantId = source.RestaurantId, Status = source.Status,
            OwnerNote = source.OwnerNote, SubmittedAt = source.SubmittedAt,
            ReviewedBy = source.ReviewedBy, ReviewedAt = source.ReviewedAt,
            ReviewNote = source.ReviewNote, CreatedAt = source.CreatedAt,
            Restaurant = Restaurant(submission),
            NewCount = changes.Count(change => change == "new"),
            EditedCount = changes.Count(change => change == "edited"),
            RemovedCount = submission.Restaurant.Dishes.Count(dish => !retained.Contains(dish.Id))
        };
    }

    /// <summary>Maps the flattened submission, proposed items, previous values and omitted dishes.</summary>
    public static ModMenuSubmissionDetailDto Detail(MenuSubmission submission)
    {
        var source = RestaurantMenuMapper.MapSubmissionDetail(submission);
        var itemsById = submission.Items.ToDictionary(x => x.Id);
        return new ModMenuSubmissionDetailDto
        {
            Id = source.Id, RestaurantId = source.RestaurantId, Status = source.Status,
            OwnerNote = source.OwnerNote, SubmittedAt = source.SubmittedAt,
            ReviewedBy = source.ReviewedBy, ReviewedAt = source.ReviewedAt,
            ReviewNote = source.ReviewNote, CreatedAt = source.CreatedAt,
            Restaurant = Restaurant(submission),
            RemovedDishes = source.RemovedDishes,
            Items = source.Items.Select(item => new ModMenuItemDiffDto
            {
                Id = item.Id, SourceDishId = item.SourceDishId, Change = item.Change,
                Name = item.Name, Description = item.Description, DishForm = item.DishForm,
                Price = item.Price, CoverMediaId = item.CoverMediaId, CoverUrl = item.CoverUrl,
                TagIds = item.TagIds, SortOrder = item.SortOrder, ResultDishId = item.ResultDishId,
                Before = item.Change == "edited" && itemsById[item.Id].SourceDish is { } dish
                    ? RestaurantMenuMapper.MapDish(dish) : null
            }).ToArray()
        };
    }

    /// <summary>Maps the restaurant fields shared by queue cards and comparison details.</summary>
    private static RestaurantMiniDto Restaurant(MenuSubmission submission) => new()
    {
        Id = submission.Restaurant.Id,
        Name = submission.Restaurant.Name,
        Ward = submission.Restaurant.Ward,
        ProvinceName = submission.Restaurant.ProvinceName,
        Latitude = submission.Restaurant.Latitude,
        Longitude = submission.Restaurant.Longitude,
        RatingAvg = submission.Restaurant.RatingAvg
    };
}
