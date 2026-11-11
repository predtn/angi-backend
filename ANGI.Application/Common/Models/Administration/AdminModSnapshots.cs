using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Models.Administration;

/// <summary>Contains the persisted fields needed to produce an AdminUserCard response.</summary>
public sealed record AdminUserSnapshot(
    int Id,
    string Email,
    string DisplayName,
    string? AvatarUrl,
    string Role,
    UserStatus Status,
    DateTime? SuspendedUntil,
    DateTime? LastLoginAt,
    DateTime CreatedAt);

/// <summary>Contains all-time moderation handling counts for one MOD.</summary>
public sealed record ModHandledSnapshot(int Verifications, int Menus, int Reports);
