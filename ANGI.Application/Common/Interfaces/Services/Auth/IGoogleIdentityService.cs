using ANGI.Application.Common.Models.Auth;

namespace ANGI.Application.Common.Interfaces.Services.Auth
{
    /// <summary>Verifies Google ID tokens and exposes only the trusted claims required by authentication.</summary>
    public interface IGoogleIdentityService
    {
        /// <summary>Validates the token signature, lifetime, audience, and verified-email claim.</summary>
        Task<GoogleIdentity> VerifyIdTokenAsync(string idToken, CancellationToken ct);
    }
}
