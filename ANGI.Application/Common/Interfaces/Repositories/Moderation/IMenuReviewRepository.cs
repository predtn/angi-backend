using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Moderation;

/// <summary>Provides persistence boundaries for menu moderation without owning business rules.</summary>
public interface IMenuReviewRepository
{
    /// <summary>Evaluates MENU_APPROVE against the current database role and permission overrides.</summary>
    Task<bool> HasPermissionAsync(int userId, CancellationToken ct);
    /// <summary>Loads a read-only queue page and its total count.</summary>
    Task<(IReadOnlyList<MenuSubmission> Items, long TotalItems)> ListAsync(ListModMenuSubmissionsRequestDto request, CancellationToken ct);
    /// <summary>Loads the complete read-only comparison graph.</summary>
    Task<MenuSubmission?> GetDetailAsync(long submissionId, CancellationToken ct);
    /// <summary>Locks the submission until the use case transaction completes and loads its menu graph.</summary>
    Task<MenuSubmission?> LockAsync(long submissionId, CancellationToken ct);
    /// <summary>Loads the reviewer for the response after assigning its identifier.</summary>
    Task LoadReviewerAsync(MenuSubmission submission, CancellationToken ct);
    /// <summary>Tracks a new dish and links its generated identifier to the proposed item.</summary>
    void AddDish(MenuSubmissionItem item, Dish dish);
    /// <summary>Replaces the tag associations of an existing tracked dish.</summary>
    void ReplaceTags(Dish dish, IReadOnlyCollection<short> tagIds);
    /// <summary>Tracks the owner notification in the current unit of work.</summary>
    void AddNotification(ANGI.Domain.Entities.Notification notification);
}
