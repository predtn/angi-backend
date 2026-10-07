using ANGI.Application.Common.Models.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Domain.Entities;

namespace ANGI.Application.UseCases.Auth
{
    /// <summary>Maps internal entities and tokens to the DTO returned by Login and Refresh.</summary>
    internal static class AuthenticationResponseMapper
    {
        /// <summary>Creates AuthResultDto from a user, token pair, and preference-survey status.</summary>
        public static AuthResultDto Map(
            User user,
            AuthenticationTokens tokens,
            bool? needsPreferenceSurvey)
        {
            return new AuthResultDto
            {
                AccessToken = tokens.AccessToken,
                AccessTokenExpiresAt = tokens.AccessTokenExpiresAt,
                RefreshToken = tokens.RefreshToken,
                RefreshTokenExpiresAt = tokens.RefreshTokenExpiresAt,
                User = new AuthUserDto
                {
                    Id = user.Id,
                    Email = user.Email,
                    DisplayName = user.DisplayName,
                    AvatarUrl = user.AvatarMedia?.Url,
                    Role = user.Role.Code,
                    Status = ToSnakeCase(user.Status.ToString()),
                    NeedsPreferenceSurvey = needsPreferenceSurvey
                }
            };
        }

        /// <summary>Converts a PascalCase enum name to snake_case for the API JSON contract.</summary>
        private static string ToSnakeCase(string value)
        {
            return string.Concat(value.Select((character, index) =>
                index > 0 && char.IsUpper(character)
                    ? $"_{char.ToLowerInvariant(character)}"
                    : char.ToLowerInvariant(character).ToString()));
        }
    }
}
