using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Moderation;

/// <summary>Persists menu moderation using explicit graph loads and submission row locks.</summary>
public sealed class MenuReviewRepository(ANGIContext context) : IMenuReviewRepository
{
    /// <summary>Evaluates the database role, role defaults and deny-preferred permission overrides.</summary>
    public Task<bool> HasPermissionAsync(int userId, CancellationToken ct) =>
        context.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => user.Role.Code == "ADMIN" ||
                (user.Role.Code == "MOD" &&
                 !context.UserPermissions.Any(p => p.UserId == userId && p.Permission.Code == "MENU_APPROVE" && p.Effect == PermissionEffect.Deny) &&
                 (user.Role.RolePermissions.Any(p => p.Permission.Code == "MENU_APPROVE") ||
                  context.UserPermissions.Any(p => p.UserId == userId && p.Permission.Code == "MENU_APPROVE" && p.Effect == PermissionEffect.Allow))))
            .SingleOrDefaultAsync(ct);

    /// <summary>Loads a filtered queue page in stable submission order with the comparison graph.</summary>
    public async Task<(IReadOnlyList<MenuSubmission> Items, long TotalItems)> ListAsync(
        ListModMenuSubmissionsRequestDto request, CancellationToken ct)
    {
        // STEP-01: Prepare status through Enum.Parse and query through AsNoTrackingWithIdentityResolution().Where.
        var status = Enum.Parse<MenuSubmissionStatus>(request.Status, true);
        var query = context.MenuSubmissions.AsNoTrackingWithIdentityResolution()
            .Where(s => s.Status == status && s.Restaurant.DeletedAt == null);
        // STEP-02: When request.RestaurantId has a value, set query.
        if (request.RestaurantId.HasValue)
            query = query.Where(s => s.RestaurantId == request.RestaurantId);
        // STEP-03: Call query.LongCountAsync and keep total.
        var total = await query.LongCountAsync(ct);
        // STEP-04: Prepare page through ThenBy(filtered).Skip().Take.
        var page = query.OrderBy(s => s.SubmittedAt).ThenBy(s => s.Id)
            .Skip(PageOffset.Of(request.Page, request.PageSize)).Take(request.PageSize);
        // STEP-05: Return the collected values.
        return (await Graph(page).ToListAsync(ct), total);
    }

    /// <summary>Loads a read-only comparison graph while excluding deleted restaurants.</summary>
    public Task<MenuSubmission?> GetDetailAsync(long submissionId, CancellationToken ct) =>
        Graph(context.MenuSubmissions.AsNoTrackingWithIdentityResolution()).SingleOrDefaultAsync(s => s.Id == submissionId, ct);

    /// <summary>Locks the same row used by owner cancellation before loading tracked menu data.</summary>
    public async Task<MenuSubmission?> LockAsync(long submissionId, CancellationToken ct)
    {
        // STEP-01: Call FromSqlInterpolated().SingleOrDefaultAsync and keep submission.
        var submission = await context.MenuSubmissions.FromSqlInterpolated($$"""
            SELECT s.* FROM core.menu_submissions s
            INNER JOIN core.restaurants r ON r.id = s.restaurant_id
            WHERE s.id = {{submissionId}} AND r.deleted_at IS NULL
            FOR UPDATE OF s
            """).SingleOrDefaultAsync(ct);
        // STEP-02: Return null early when submission is missing.
        if (submission is null) return null;
        // STEP-03: Return the result of Graph().SingleAsync.
        return await Graph(context.MenuSubmissions).SingleAsync(s => s.Id == submissionId, ct);
    }

    /// <summary>Loads the assigned reviewer and avatar before mapping the successful decision.</summary>
    public async Task LoadReviewerAsync(MenuSubmission submission, CancellationToken ct)
    {
        // STEP-01: Prepare reference through context.Entry().Reference.
        var reference = context.Entry(submission).Reference(s => s.Reviewer);
        // STEP-02: Update reference: IsLoaded.
        reference.IsLoaded = false;
        // STEP-03: Load Query().Include(filtered).LoadAsync.
        await reference.Query().Include(user => user!.AvatarMedia).LoadAsync(ct);
    }

    /// <summary>Tracks the new dish and lets EF propagate its identity to result_dish_id in the single save.</summary>
    public void AddDish(MenuSubmissionItem item, Dish dish)
    {
        context.Dishes.Add(dish);
        item.ResultDish = dish;
    }

    /// <summary>Removes obsolete tag links and adds missing links while retaining common composite keys.</summary>
    public void ReplaceTags(Dish dish, IReadOnlyCollection<short> tagIds)
    {
        // STEP-01: For each tag in dish.DishTags.Where(filtered).ToArray(), call context.DishTags.Remove.
        foreach (var tag in dish.DishTags.Where(tag => !tagIds.Contains(tag.TagId)).ToArray())
            context.DishTags.Remove(tag);
        // STEP-02: Prepare existing through dish.DishTags.Select(filtered).ToHashSet.
        var existing = dish.DishTags.Select(tag => tag.TagId).ToHashSet();
        // STEP-03: For each id in tagIds.Where(filtered), call dish.DishTags.Add.
        foreach (var id in tagIds.Where(id => !existing.Contains(id)))
            dish.DishTags.Add(new DishTag { DishId = dish.Id, TagId = id });
    }

    /// <summary>Stages an owner notification without saving independently.</summary>
    public void AddNotification(ANGI.Domain.Entities.Notification notification) => context.Notifications.Add(notification);

    /// <summary>Loads the complete comparison graph explicitly and splits collection joins.</summary>
    private static IQueryable<MenuSubmission> Graph(IQueryable<MenuSubmission> query) =>
        query.Include(s => s.Reviewer).ThenInclude(u => u!.AvatarMedia)
            .Include(s => s.Items).ThenInclude(i => i.CoverMedia)
            .Include(s => s.Items).ThenInclude(i => i.SourceDish).ThenInclude(d => d!.DishTags).ThenInclude(t => t.Tag)
            .Include(s => s.Items).ThenInclude(i => i.SourceDish).ThenInclude(d => d!.CoverMedia)
            .Include(s => s.Restaurant).ThenInclude(r => r.Dishes.Where(d => d.Status != DishStatus.Removed)).ThenInclude(d => d.CoverMedia)
            .Include(s => s.Restaurant).ThenInclude(r => r.Dishes.Where(d => d.Status != DishStatus.Removed)).ThenInclude(d => d.DishTags).ThenInclude(t => t.Tag)
            .AsSplitQuery();
}
