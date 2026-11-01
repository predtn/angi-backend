using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.Repositories.Moderation;

/// <summary>Repository for querying user accounts and details for moderation purposes.</summary>
public interface IUserModerationRepository
{
    /// <summary>Lists non-staff users matching the given filters and pagination.</summary>
    Task<Paged<AdminUserCardDto>> ListAsync(ListUsersRequestDto request, CancellationToken ct);

    /// <summary>Gets complete account moderation details including sanctions, stats and restaurant.</summary>
    Task<ModUserDetailDto?> GetDetailAsync(int userId, CancellationToken ct);
}
