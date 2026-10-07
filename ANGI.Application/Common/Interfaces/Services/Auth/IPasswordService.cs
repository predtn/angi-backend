namespace ANGI.Application.Common.Interfaces.Services.Auth
{
    /// <summary>Keeps the password-verification algorithm outside the Application layer.</summary>
    public interface IPasswordService
    {
        /// <summary>Compares a plain-text password with the stored password hash.</summary>
        bool Verify(string password, string passwordHash);
    }
}
