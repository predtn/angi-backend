using ANGI.Application.Common.Interfaces.Services.Auth;

namespace ANGI.Infrastructure.Services.Auth
{
    /// <summary>Implements password-hash verification with BCrypt.</summary>
    public sealed class PasswordService : IPasswordService
    {
        /// <summary>Returns true when the plain-text password matches the stored BCrypt hash.</summary>
        public bool Verify(string password, string passwordHash)
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
    }
}
