using ANGI.Application.Common.Interfaces.Services.Auth;

namespace ANGI.Infrastructure.Services.Auth
{
    /// <summary>Implements password hashing and verification with BCrypt.</summary>
    public sealed class PasswordService : IPasswordService
    {
        /// <summary>Creates a salted BCrypt hash using the library's current secure work factor.</summary>
        public string Hash(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password);
        }

        /// <summary>Returns true when the plain-text password matches the stored BCrypt hash.</summary>
        public bool Verify(string password, string passwordHash)
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
    }
}
