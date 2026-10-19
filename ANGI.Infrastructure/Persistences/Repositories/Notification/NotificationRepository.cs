using ANGI.Application.Common.Interfaces.Repositories.Notification;
using ANGI.Domain.Entities;
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
