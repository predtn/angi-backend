using System.Text.Json;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Administration;

/// <summary>Maps MOD persistence records to administration API contracts.</summary>
internal static class AdministrationModMapper
{
    /// <summary>Maps a page of repository snapshots while preserving all pagination metadata.</summary>
    public static Paged<AdminUserCardDto> ToPagedDto(Paged<AdminUserSnapshot> page) => new()
    {
        Items = page.Items.Select(ToCardDto).ToArray(),
        Page = page.Page,
        PageSize = page.PageSize,
        TotalItems = page.TotalItems,
        TotalPages = page.TotalPages
    };

    /// <summary>Maps a read-only MOD snapshot to the shared administration card.</summary>
    public static AdminUserCardDto ToCardDto(AdminUserSnapshot snapshot) => new()
    {
        Id = snapshot.Id,
        Email = snapshot.Email,
        DisplayName = snapshot.DisplayName,
        AvatarUrl = snapshot.AvatarUrl,
        Role = snapshot.Role,
        Status = ToApiStatus(snapshot.Status),
        SuspendedUntil = snapshot.SuspendedUntil,
        LastLoginAt = snapshot.LastLoginAt,
        CreatedAt = snapshot.CreatedAt
    };

    /// <summary>Maps a tracked MOD entity after creation or update to the shared administration card.</summary>
    public static AdminUserCardDto ToCardDto(User user, string roleCode) => new()
    {
        Id = user.Id,
        Email = user.Email,
        DisplayName = user.DisplayName,
        AvatarUrl = user.AvatarMedia?.Url,
        Role = roleCode,
        Status = ToApiStatus(user.Status),
        SuspendedUntil = user.SuspendedUntil,
        LastLoginAt = user.LastLoginAt,
        CreatedAt = user.CreatedAt
    };

    /// <summary>Converts a domain account status to the documented snake_case API value.</summary>
    private static string ToApiStatus(UserStatus status) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());
}
