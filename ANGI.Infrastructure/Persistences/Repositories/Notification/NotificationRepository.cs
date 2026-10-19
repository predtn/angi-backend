using ANGI.Application.Common.Interfaces.Repositories.Notification;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Notification;

/// <summary>Implements persistence operations for querying notifications.</summary>
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
            .Skip((page - 1) * pageSize)
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
}
