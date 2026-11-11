namespace ANGI.Application.Common.Models.Auth
{
    /// <summary>Contains an opaque user token and the one-way hash safe to persist.</summary>
    public sealed record GeneratedUserToken(string Token, string TokenHash);
}
