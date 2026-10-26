using ANGI.Application.Common.Interfaces.Repositories.Notification;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Notification;

/// <summary>Implements persistence operations for notifications using EF Core.</summary>
public sealed class NotificationRepository : INotificationRepository
{
    private readonly ANGIContext _context;

    public NotificationRepository(ANGIContext context)
    {
        _context = context;
    }

    public async Task<(IReadOnlyList<Domain.Entities.Notification> Items, long TotalItems)> GetNotificationsAsync(
        int userId,
        bool? isRead,
        string? type,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var query = _context.Notifications
            .AsNoTracking()
            .Where(n => n.RecipientId == userId);

        if (isRead.HasValue)
        {
            query = isRead.Value
                ? query.Where(n => n.ReadAt != null)
                : query.Where(n => n.ReadAt == null);
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            query = query.Where(n => n.Type == type);
        }

        var totalItems = await query.LongCountAsync(ct);

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip(PageOffset.Of(page, pageSize))
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalItems);
    }

    public async Task<int> GetUnreadCountAsync(int userId, CancellationToken ct)
    {
        return await _context.Notifications
            .AsNoTracking()
            .Where(n => n.RecipientId == userId && n.ReadAt == null)
            .CountAsync(ct);
    }

    public async Task<List<Domain.Entities.Notification>> GetUnreadNotificationsToMarkReadAsync(
        int userId,
        IReadOnlyList<long>? ids,
        bool markAll,
        CancellationToken ct)
    {
        var query = _context.Notifications
            .Where(n => n.RecipientId == userId && n.ReadAt == null);

        if (!markAll)
        {
            if (ids is null || ids.Count == 0)
            {
                return [];
            }

            query = query.Where(n => ids.Contains(n.Id));
        }

        return await query.ToListAsync(ct);
    }
}
