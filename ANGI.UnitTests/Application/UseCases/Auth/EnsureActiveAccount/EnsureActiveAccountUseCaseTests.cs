using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.UseCases.Auth.EnsureActiveAccount;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Auth.EnsureActiveAccount
{
    /// <summary>Verifies EnsureActiveAccountUseCase lets only active accounts through and maps every other status to its AUTH-04 error.</summary>
    public sealed class EnsureActiveAccountUseCaseTests
    {
        // TEST-01: Let an active account through.
        /// <summary>Ensures an active account is not blocked by the status guard.</summary>
        [Fact]
        public async Task ExecuteAsync_WithActiveAccount_ShouldReturn()
        {
            var useCase = CreateUseCase(new AccountStatusSnapshot(UserStatus.Active, null));

            var act = () => useCase.ExecuteAsync(12, CancellationToken.None);

            await act.Should().NotThrowAsync();
        }

        // TEST-02: Reject a suspended account with its suspension end, like AUTH-04.
        /// <summary>Ensures a suspended user learns when the suspension ends.</summary>
        [Fact]
        public async Task ExecuteAsync_WithSuspendedAccount_ShouldThrowAccountSuspended()
        {
            var suspendedUntil = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
            var useCase = CreateUseCase(new AccountStatusSnapshot(UserStatus.Suspended, suspendedUntil));

            var act = () => useCase.ExecuteAsync(12, CancellationToken.None);

            (await act.Should().ThrowAsync<AccountSuspendedException>())
                .Which.SuspendedUntil.Should().Be(suspendedUntil);
        }

        // TEST-03: Reject banned, deactivated and unverified accounts with the AUTH-04 codes.
        /// <summary>Ensures every blocked status is rejected with the error code clients rely on.</summary>
        [Theory]
        [InlineData(UserStatus.Banned, "ACCOUNT_BANNED")]
        [InlineData(UserStatus.Deactivated, "ACCOUNT_DEACTIVATED")]
        [InlineData(UserStatus.PendingVerification, "EMAIL_NOT_VERIFIED")]
        public async Task ExecuteAsync_WithInactiveAccount_ShouldThrowForbidden(UserStatus status, string errorCode)
        {
            var useCase = CreateUseCase(new AccountStatusSnapshot(status, null));

            var act = () => useCase.ExecuteAsync(12, CancellationToken.None);

            (await act.Should().ThrowAsync<ForbiddenException>()).Which.ErrorCode.Should().Be(errorCode);
        }

        // TEST-04: Treat a deleted or unknown user as not signed in.
        /// <summary>Ensures a token for a removed account is treated as signed out rather than forbidden.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUnknownUser_ShouldThrowUnauthorized()
        {
            var useCase = CreateUseCase(null);

            var act = () => useCase.ExecuteAsync(12, CancellationToken.None);

            (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        }

        /// <summary>Creates the use case over a status cache that returns the given snapshot for user 12.</summary>
        private static EnsureActiveAccountUseCase CreateUseCase(AccountStatusSnapshot? account)
        {
            var cache = new Mock<IAccountStatusCache>();
            cache.Setup(x => x.GetAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync(account);
            return new EnsureActiveAccountUseCase(cache.Object);
        }
    }
}
