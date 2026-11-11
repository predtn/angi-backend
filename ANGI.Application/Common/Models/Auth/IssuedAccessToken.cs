namespace ANGI.Application.Common.Models.Auth
{
    /// <summary>A signed access token (JWT) and its expiry.</summary>
    public sealed record IssuedAccessToken(string Token, DateTime ExpiresAt);
}
