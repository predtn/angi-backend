using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Account;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.UseCases.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation;

/// <summary>Verifies database permission checks independently of HTTP role authorization.</summary>
public sealed class ModerationAccessTests
{
    // TEST-01: Admin bypasses Mod overrides and does not query effective permissions.
    /// <summary>Verifies the documented all-permissions behavior of Admin accounts.</summary>
    [Fact]
    public async Task AdminHasEveryPermission()
    {
        var world = new ModerationTestContext();
        world.Current.SetupGet(x => x.Role).Returns("ADMIN");
        (await world.Access.RequireAsync(default, "RESTAURANT_MODERATE")).Should().Be(3);
        world.Accounts.VerifyNoOtherCalls();
    }

    // TEST-02: Read access accepts either verification or moderation permission.
    /// <summary>Verifies OR semantics and cancellation forwarding for effective permissions.</summary>
    [Theory]
    [InlineData("RESTAURANT_VERIFY")]
    [InlineData("RESTAURANT_MODERATE")]
    public async Task ModRequiresAtLeastOneRequestedPermission(string permission)
    {
        var world = new ModerationTestContext();
        using var cancellation = new CancellationTokenSource();
        world.Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", cancellation.Token)).ReturnsAsync([permission]);
        (await world.Access.RequireAsync(cancellation.Token, "RESTAURANT_MODERATE", "RESTAURANT_VERIFY")).Should().Be(3);
    }

    // TEST-03: Missing identity, wrong role and denied permissions never authorize an operation.
    /// <summary>Checks authentication and privilege boundaries without trusting claims for permissions.</summary>
    [Theory]
    [InlineData(null, null, "UNAUTHORIZED")]
    [InlineData(3, "TRAVELER", "FORBIDDEN")]
    [InlineData(3, "RESTAURANT_OWNER", "FORBIDDEN")]
    [InlineData(3, "MOD", "FORBIDDEN")]
    public async Task RejectsUnauthorizedCallers(int? id, string? role, string code)
    {
        var world = new ModerationTestContext();
        world.Current.SetupGet(x => x.UserId).Returns(id);
        world.Current.SetupGet(x => x.Role).Returns(role);
        world.Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var action = () => world.Access.RequireAsync(default, "RESTAURANT_MODERATE");
        (await action.Should().ThrowAsync<AppException>()).Which.ErrorCode.Should().Be(code);
    }
}

/// <summary>Supplies isolated mocks and a fixed clock to the mirrored moderation use-case tests.</summary>
internal sealed class ModerationTestContext
{
    internal Mock<ICurrentUserService> Current { get; } = new();
    internal Mock<IAccountRepository> Accounts { get; } = new();
    internal Mock<IRestaurantModerationRepository> Repository { get; } = new();
    internal Mock<IUnitOfWork> UnitOfWork { get; } = new();
    internal Mock<IUnitOfWorkTransaction> Transaction { get; } = new();
    internal Mock<IAuditLogService> Audit { get; } = new();
    internal Mock<ICloudinaryService> Cloudinary { get; } = new();
    internal Mock<IAccountStatusCache> Cache { get; } = new();
    internal TimeProvider Clock { get; } = new FixedClock();
    internal ModerationAccess Access => new(Current.Object, Accounts.Object);
    internal RestaurantModerationEffects Effects => new(Repository.Object, Audit.Object);
    internal Domain.Entities.Restaurant Restaurant { get; } = new()
    {
        Id = 7, OwnerId = 12, Name = "Restaurant", Slug = "restaurant", AddressLine = "1 Street",
        Owner = new User { Id = 12, Email = "owner@test.local", DisplayName = "Owner", Role = new Role { Code = "RESTAURANT_OWNER" } }
    };

    /// <summary>Configures deterministic identity, permissions, transactions and display metadata.</summary>
    internal ModerationTestContext()
    {
        Current.SetupGet(x => x.UserId).Returns(3);
        Current.SetupGet(x => x.Role).Returns("MOD");
        Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["RESTAURANT_MODERATE", "RESTAURANT_VERIFY", "REPORT_RESTAURANT_HANDLE", "REPORT_SOCIAL_HANDLE", "USER_SUSPEND", "USER_BAN"]);
        UnitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
        Repository.Setup(x => x.LockRestaurantAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Restaurant);
        Repository.Setup(x => x.GetReportDisplayDataAsync(It.IsAny<ReportTarget>(), It.IsAny<IReadOnlyList<Report>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReportDisplayData("Restaurant", new Dictionary<int, User>
            {
                [3] = new() { Id = 3, DisplayName = "Mod" }, [4] = new() { Id = 4, DisplayName = "Reporter" }
            }, new Dictionary<long, MediaFile>(), 0));
    }

    /// <summary>Returns a fixed UTC instant for deadline, audit and sanction assertions.</summary>
    private sealed class FixedClock : TimeProvider
    {
        /// <summary>Returns the test suite's fixed UTC instant.</summary>
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
    }
}
