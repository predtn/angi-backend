using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Auth;
using Google.Apis.Auth;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Auth
{
    /// <summary>Validates Google ID tokens with Google's signing keys and the configured OAuth audience.</summary>
    public sealed class GoogleIdentityService : IGoogleIdentityService
    {
        private const string InvalidTokenCode = "GOOGLE_TOKEN_INVALID";
        private const string InvalidTokenMessage = "Google ID token không hợp lệ.";

        private readonly GoogleSettings _settings;
        private readonly Func<string, GoogleJsonWebSignature.ValidationSettings,
            Task<GoogleJsonWebSignature.Payload>> _validateToken;

        /// <summary>Initializes Google verification with the configured web-client audience.</summary>
        public GoogleIdentityService(IOptions<GoogleSettings> settings)
            : this(settings, GoogleJsonWebSignature.ValidateAsync)
        {
        }

        /// <summary>Allows the Google validator boundary to be replaced by deterministic infrastructure tests.</summary>
        internal GoogleIdentityService(
            IOptions<GoogleSettings> settings,
            Func<string, GoogleJsonWebSignature.ValidationSettings,
                Task<GoogleJsonWebSignature.Payload>> validateToken)
        {
            _settings = settings.Value;
            _validateToken = validateToken;
        }

        /// <summary>Validates signature, expiry, audience, subject, email, and Google's email-verification claim.</summary>
        public async Task<GoogleIdentity> VerifyIdTokenAsync(string idToken, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_settings.ClientId))
            {
                throw InvalidToken();
            }

            GoogleJsonWebSignature.Payload payload;
            try
            {
                payload = await _validateToken(
                        idToken,
                        new GoogleJsonWebSignature.ValidationSettings
                        {
                            Audience = [_settings.ClientId]
                        })
                    .WaitAsync(ct);
            }
            catch (InvalidJwtException)
            {
                throw InvalidToken();
            }

            if (string.IsNullOrWhiteSpace(payload.Subject) ||
                string.IsNullOrWhiteSpace(payload.Email) ||
                !payload.EmailVerified)
            {
                throw InvalidToken();
            }

            return new GoogleIdentity(payload.Subject, payload.Email, payload.Name);
        }

        /// <summary>Creates the documented AUTH-05 error without exposing validator details.</summary>
        private static UnauthorizedException InvalidToken() =>
            new(InvalidTokenCode, InvalidTokenMessage);
    }
}
