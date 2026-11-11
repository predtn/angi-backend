using ANGI.Application.Common.Models.Auth;

namespace ANGI.Application.Common.Interfaces.Services.Auth
{
    /// <summary>Reads a user's account status from the database through a short cache.</summary>
    public interface IAccountStatusCache
    {
        /// <summary>Gets the status, or null when the user does not exist or is soft-deleted.</summary>
        Task<AccountStatusSnapshot?> GetAsync(int userId, CancellationToken ct);

        /// <summary>Drops the cached status; every use case that changes users.status must call it.</summary>
        void Invalidate(int userId);
    }
}
