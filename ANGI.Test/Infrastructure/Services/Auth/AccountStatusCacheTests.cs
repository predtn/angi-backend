using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Models.Auth;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Services.Auth;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace ANGI.Test.Infrastructure.Services.Auth
{
    public sealed class AccountStatusCacheTests
    {
        private readonly Mock<IAuthenticationRepository> _repository = new();
        private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());

        // TEST-01: Read the database once and serve the next requests from the cache.
        [Fact]
        public async Task GetAsync_CalledTwice_ShouldReadDatabaseOnce()
        {
            SetupStatus(UserStatus.Active);
            var cache = new AccountStatusCache(_memoryCache, _repository.Object);

            await cache.GetAsync(12, CancellationToken.None);
            var account = await cache.GetAsync(12, CancellationToken.None);

            account!.Status.Should().Be(UserStatus.Active);
            _repository.Verify(x => x.GetAccountStatusAsync(12, It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-02: After Invalidate, the next request sees the new status.
        [Fact]
        public async Task GetAsync_AfterInvalidate_ShouldReturnNewStatus()
        {
            SetupStatus(UserStatus.Active);
            var cache = new AccountStatusCache(_memoryCache, _repository.Object);
            await cache.GetAsync(12, CancellationToken.None);

            SetupStatus(UserStatus.Banned);
            cache.Invalidate(12);
            var account = await cache.GetAsync(12, CancellationToken.None);

            account!.Status.Should().Be(UserStatus.Banned);
        }

        // TEST-03: Do not cache a missing user, so a later insert is seen immediately.
        [Fact]
        public async Task GetAsync_WithMissingUser_ShouldNotCacheNull()
        {
            _repository.Setup(x => x.GetAccountStatusAsync(12, It.IsAny<CancellationToken>()))
                .ReturnsAsync((AccountStatusSnapshot?)null);
            var cache = new AccountStatusCache(_memoryCache, _repository.Object);

            await cache.GetAsync(12, CancellationToken.None);
            await cache.GetAsync(12, CancellationToken.None);

            _repository.Verify(x => x.GetAccountStatusAsync(12, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        private void SetupStatus(UserStatus status)
        {
            _repository.Setup(x => x.GetAccountStatusAsync(12, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccountStatusSnapshot(status, null));
        }
    }
}
