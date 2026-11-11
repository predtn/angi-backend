namespace ANGI.Application.Common.Interfaces.Services.Auth
{
    /// <summary>Keeps password hashing and verification outside the Application layer.</summary>
    public interface IPasswordService
    {
        /// <summary>Creates a BCrypt hash suitable for storage in users.password_hash.</summary>
        string Hash(string password);

        /// <summary>Compares a plain-text password with the stored password hash.</summary>
        bool Verify(string password, string passwordHash);
    }
}
