namespace ANGI.Application.Common.Models.Auth
{
    /// <summary>A new opaque refresh token, the hash stored in user_sessions, and its expiry.</summary>
    public sealed record GeneratedRefreshToken(string Token, string TokenHash, DateTime ExpiresAt);
}
