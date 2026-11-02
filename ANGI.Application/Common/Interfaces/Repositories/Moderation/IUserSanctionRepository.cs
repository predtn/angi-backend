using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Moderation;

/// <summary>Defines common persistence operations required for applying user sanctions.</summary>
public interface IUserSanctionRepository
{
    /// <summary>Locks the account being sanctioned, including its current database role.</summary>
    Task<User?> LockUserAsync(int userId, CancellationToken ct);

    /// <summary>Loads unrevoked sessions for a sanctioned user.</summary>
    Task<IReadOnlyList<UserSession>> GetSessionsAsync(int userId, CancellationToken ct);

    /// <summary>Adds a sanction to the current unit of work.</summary>
    void AddSanction(UserSanction sanction);

    /// <summary>Adds a notification to the current unit of work.</summary>
    void AddNotification(Domain.Entities.Notification notification);
}
