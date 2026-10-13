using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Account;

/// <summary>Provides persistence operations required by Account / Personal Profile use cases.</summary>
public interface IAccountRepository
{
    /// <summary>Loads user and avatar media as read-only (no tracking) for ME-01.</summary>
    Task<User?> GetProfileUserAsync(int userId, CancellationToken ct);

    /// <summary>Loads tracked user and avatar media for ME-02 partial updates.</summary>
    Task<User?> GetUserForUpdateAsync(int userId, CancellationToken ct);

    /// <summary>Gets external login providers linked to the user account.</summary>
    Task<IReadOnlyList<string>> GetLinkedProvidersAsync(int userId, CancellationToken ct);

    /// <summary>Gets the restaurant identifier owned by the user when role is RESTAURANT_OWNER.</summary>
    Task<long?> GetOwnerRestaurantIdAsync(int userId, CancellationToken ct);

    /// <summary>Gets effective permissions for the user (Mod/Admin permissions, empty for other roles).</summary>
    Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(int userId, string roleCode, CancellationToken ct);
}
