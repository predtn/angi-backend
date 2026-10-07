using ANGI.Application.Common.Models.Auth;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Services.Auth
{
    /// <summary>Provides JWT issuance, refresh-token generation, and refresh-token hashing.</summary>
    public interface IAuthenticationTokenService
    {
        /// <summary>Creates an access-token and refresh-token pair for a user.</summary>
        AuthenticationTokens CreateTokens(User user);

        /// <summary>Hashes a refresh token before it is queried or stored in the database.</summary>
        string HashRefreshToken(string refreshToken);
    }
}
