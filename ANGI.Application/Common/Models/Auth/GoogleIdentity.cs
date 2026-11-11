namespace ANGI.Application.Common.Models.Auth
{
    /// <summary>Contains the trusted identity claims extracted from a verified Google ID token.</summary>
    public sealed record GoogleIdentity(string ProviderUserId, string Email, string? DisplayName);
}
