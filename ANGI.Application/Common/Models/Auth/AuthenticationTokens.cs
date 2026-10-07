namespace ANGI.Application.Common.Models.Auth
{
    public sealed record AuthenticationTokens(
        string AccessToken,
        DateTime AccessTokenExpiresAt,
        string RefreshToken,
        string RefreshTokenHash,
        DateTime RefreshTokenExpiresAt);
}
