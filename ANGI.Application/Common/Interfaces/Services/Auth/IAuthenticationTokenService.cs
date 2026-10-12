using ANGI.Application.Common.Models.Auth;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Services.Auth
{
    /// <summary>Provides JWT issuance and secure opaque token generation and hashing.</summary>
    public interface IAuthenticationTokenService
    {
        /// <summary>Creates an access-token and refresh-token pair for a user.</summary>
        AuthenticationTokens CreateTokens(User user);

        /// <summary>Hashes a refresh token before it is queried or stored in the database.</summary>
        string HashRefreshToken(string refreshToken);

        /// <summary>Creates a cryptographically secure opaque token for a one-time user action.</summary>
        string CreateUserToken();

        /// <summary>Hashes a one-time user token before it is stored in user_tokens.</summary>
        string HashUserToken(string token);
    }
}
