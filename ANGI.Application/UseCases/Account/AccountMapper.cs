using ANGI.Application.DTOs.Account;
using ANGI.Domain.Entities;

namespace ANGI.Application.UseCases.Account;

/// <summary>Maps internal user entities and related metadata to UserProfileDto.</summary>
internal static class AccountMapper
{
    public static UserProfileDto Map(
        User user,
        string? avatarUrl,
        IReadOnlyList<string> linkedProviders,
        string? preferenceStatus,
        long? restaurantId,
        IReadOnlyList<string> permissions)
    {
        return new UserProfileDto
        {
            Id = user.Id,
            Email = user.Email,
            Role = user.Role.Code,
            DisplayName = user.DisplayName,
            AvatarUrl = avatarUrl,
            Phone = user.Phone,
            Bio = user.Bio,
            Status = ToSnakeCase(user.Status.ToString()),
            EmailVerified = user.EmailVerifiedAt.HasValue,
            HasPassword = user.PasswordHash is not null,
            LinkedProviders = linkedProviders,
            PreferenceStatus = preferenceStatus,
            RestaurantId = restaurantId,
            Permissions = permissions,
            CreatedAt = user.CreatedAt
        };
    }

    /// <summary>Converts a PascalCase enum name to snake_case for the API JSON contract.</summary>
    public static string ToSnakeCase(string value)
    {
        return string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? $"_{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));
    }
}
