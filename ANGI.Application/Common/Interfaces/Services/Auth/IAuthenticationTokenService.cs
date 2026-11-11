using ANGI.Application.Common.Models.Auth;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Services.Auth
{
    /// <summary>Provides JWT issuance and secure opaque-token generation and hashing.</summary>
    public interface IAuthenticationTokenService
    {
        /// <summary>
        /// Creates the refresh token of a new session; its hash is stored in user_sessions before the
        /// access token is issued.
        /// </summary>
        GeneratedRefreshToken CreateRefreshToken();

        /// <summary>
        /// Creates the access token of a saved session. The token carries the session id as claim sid,
        /// so the API knows which session a request comes from (ME-03 keeps it).
        /// </summary>
        IssuedAccessToken CreateAccessToken(User user, long sessionId);

        /// <summary>Hashes a refresh token before it is queried or stored in the database.</summary>
        string HashRefreshToken(string refreshToken);

        /// <summary>Creates a random user token and the SHA-256 hash used to persist it.</summary>
        GeneratedUserToken CreateUserToken();

        /// <summary>Hashes a user token before it is queried in the database.</summary>
        string HashUserToken(string token);
    }
}
