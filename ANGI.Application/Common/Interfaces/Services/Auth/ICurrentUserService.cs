using System.Net;

namespace ANGI.Application.Common.Interfaces.Services.Auth
{
    /// <summary>Provides the current HTTP request identity and metadata to the Application layer.</summary>
    public interface ICurrentUserService
    {
        /// <summary>Gets the user id from the sub claim, or null when the request is unauthenticated.</summary>
        int? UserId { get; }

        /// <summary>Gets the role code from the role claim, or null when the request is unauthenticated.</summary>
        string? Role { get; }

        /// <summary>Gets the User-Agent value of the current request.</summary>
        string? UserAgent { get; }

        /// <summary>Gets the client IP address of the current request.</summary>
        IPAddress? IpAddress { get; }
    }
}
