using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Auth;
using Microsoft.Extensions.Caching.Memory;

namespace ANGI.Infrastructure.Services.Auth
{
    /// <summary>Caches users.status for 30 s so the per-request account check rarely reaches the database.</summary>
    public sealed class AccountStatusCache : IAccountStatusCache
    {
        internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

        private readonly IMemoryCache _cache;
        private readonly IAuthenticationRepository _authenticationRepository;

        /// <summary>Initializes the cache with the shared memory cache and the Auth repository.</summary>
        public AccountStatusCache(IMemoryCache cache, IAuthenticationRepository authenticationRepository)
        {
            _cache = cache;
            _authenticationRepository = authenticationRepository;
        }

        /// <summary>Returns the cached status or loads it; a missing user is not cached.</summary>
        public async Task<AccountStatusSnapshot?> GetAsync(int userId, CancellationToken ct)
        {
            var key = Key(userId);
            if (_cache.TryGetValue(key, out AccountStatusSnapshot? cached) && cached is not null)
            {
                return cached;
            }

            var account = await _authenticationRepository.GetAccountStatusAsync(userId, ct);
            if (account is not null)
            {
                _cache.Set(key, account, Duration);
            }

            return account;
        }

        /// <summary>Drops the cached status so the next request reads the new one.</summary>
        public void Invalidate(int userId)
        {
            _cache.Remove(Key(userId));
        }

        private static string Key(int userId) => $"account-status:{userId}";
    }
}
